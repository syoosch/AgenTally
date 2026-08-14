using System.Runtime.CompilerServices;
using System.Text;
using System.IO;
using System.Reflection;
using System.Globalization;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Hosting;
using AgenTally.Core.Monitoring;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
public sealed class CollectionSchedulerTests
{
    [TestMethod]
    public async Task Scheduler_CoalescesFileStormAndDisposalStopsMonitoring()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-scheduler-test.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", new UTF8Encoding(false));

        var collector = new CountingCollector(codexHome, rollout);
        var writer = new CursorWriter();
        var scheduler = new CollectionScheduler(
            collector,
            writer,
            new CollectorContext(directory.Path, TimeProvider.System));

        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => collector.CollectionCount == 1);

        for (int index = 0; index < 5; index++)
        {
            await File.AppendAllTextAsync(
                rollout,
                $"{{\"change\":{index}}}\n",
                new UTF8Encoding(false));
        }

        await WaitUntilAsync(() => collector.CollectionCount == 2);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.AreEqual(2, collector.CollectionCount);
        Assert.AreEqual(2, writer.CommitCount);
        Assert.IsNull(collector.SeenCursors[0]);
        Assert.AreEqual("cursor-1", collector.SeenCursors[1]?.CursorJson);

        await scheduler.DisposeAsync();
        await File.AppendAllTextAsync(rollout, "{\"afterDispose\":true}\n");
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.AreEqual(2, collector.CollectionCount);
        Assert.IsTrue(scheduler.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task StartAsync_OldParserStateStopsBeforeAnyCollectionOrWatcherWork()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(codexHome, "sessions", "rollout-old-parser.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var writer = new CursorWriter { RequiresRebuild = true };
        var scheduler = new CollectionScheduler(
            collector,
            writer,
            new CollectorContext(directory.Path, TimeProvider.System));

        await Assert.ThrowsExactlyAsync<CodexParserRebuildRequiredException>(async () =>
            await scheduler.StartAsync(CancellationToken.None));

        Assert.AreEqual(0, collector.CollectionCount);
        Assert.AreEqual(0, writer.CommitCount);
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_ProbeDiagnosticStopsBeforeAnyCollectionOrWatcherWork()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(codexHome, "sessions", "rollout-probe-failure.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout)
        {
            ReportProbeDiagnostic = true
        };
        var writer = new CursorWriter();
        var scheduler = new CollectionScheduler(
            collector,
            writer,
            new CollectorContext(directory.Path, TimeProvider.System));

        SourceProbeIncompleteException exception =
            await Assert.ThrowsExactlyAsync<SourceProbeIncompleteException>(async () =>
                await scheduler.StartAsync(CancellationToken.None));

        Assert.AreEqual(1, exception.DiagnosticCount);
        Assert.AreEqual(0, collector.CollectionCount);
        Assert.AreEqual(0, writer.CommitCount);
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Scheduler_StartupImportContinuesPastCollectorBatchLimitToEndOfFile()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "2026",
            "07",
            "16",
            "rollout-scheduler-large.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);

        var content = new StringBuilder();
        content.AppendLine(
            "{\"timestamp\":\"2026-07-16T01:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"scheduler-large-thread\",\"model_provider\":\"openai\"}}");
        DateTimeOffset startedAt = new(2026, 7, 16, 1, 0, 1, TimeSpan.Zero);
        const int tokenEventCount = 5_201;
        for (int index = 1; index <= tokenEventCount; index++)
        {
            content.Append("{\"timestamp\":\"")
                .Append(startedAt.AddMilliseconds(index).ToString("O", CultureInfo.InvariantCulture))
                .Append("\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"input_tokens\":1,\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":1},\"total_token_usage\":{\"input_tokens\":")
                .Append(index)
                .Append(",\"cached_input_tokens\":0,\"output_tokens\":0,\"total_tokens\":")
                .Append(index)
                .AppendLine("}}}}");
        }

        await File.WriteAllTextAsync(rollout, content.ToString(), Utf8WithoutBom);
        var writer = new CursorWriter();
        var scheduler = new CollectionScheduler(
            new CodexCollector(codexHome),
            writer,
            new CollectorContext(directory.Path, TimeProvider.System));

        await scheduler.StartAsync(CancellationToken.None);
        bool completed = await WaitUntilOrTimeoutAsync(
            () => writer.AppliedEventCount == tokenEventCount,
            TimeSpan.FromSeconds(15));
        StoredCursor? cursor = await writer.GetCursorAsync(
            CodexSourceIdentity.InstanceId(codexHome),
            CodexSourceIdentity.EntityId(rollout),
            CancellationToken.None);
        await scheduler.DisposeAsync();

        Assert.IsTrue(completed);
        Assert.AreEqual(tokenEventCount, writer.AppliedEventCount);
        Assert.IsNotNull(cursor);
        CodexCursor parsed = CodexCursor.DeserializeOrStart(
            cursor.CursorJson,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);
        Assert.IsNull(diagnostic);
        Assert.AreEqual(new FileInfo(rollout).Length, parsed.Jsonl.ByteOffset);
    }

    [TestMethod]
    public async Task Scheduler_BatchLimitWithoutCursorProgressFaultsInsteadOfLoopingForever()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        var collector = new StalledBatchLimitCollector(codexHome);
        var scheduler = new CollectionScheduler(
            collector,
            new CursorWriter(),
            new CollectorContext(directory.Path, TimeProvider.System));

        await scheduler.StartAsync(CancellationToken.None);
        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.AreEqual(
            "Collection cursor did not advance after reaching the batch limit.",
            exception.Message);
        Assert.AreEqual(2, collector.CollectionCount);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await scheduler.DisposeAsync().AsTask());
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task StartAsync_MoreThanQueueCapacityPropagatesEarlyConsumerFault()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        var collector = new EarlyFailingCollector(codexHome, entityCount: 300);
        var scheduler = new CollectionScheduler(
            collector,
            new CursorWriter(),
            new CollectorContext(directory.Path, TimeProvider.System));

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await scheduler.StartAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.AreEqual("early sync failure", exception.Message);
        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task Scheduler_PropagatesUnexpectedWatcherBackgroundFailure()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(
            codexHome,
            "sessions",
            "rollout-monitor-fault.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var writer = new CursorWriter();
        var scheduler = new CollectionScheduler(
            collector,
            writer,
            new CollectorContext(directory.Path, TimeProvider.System));
        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => collector.CollectionCount == 1);

        writer.ThrowOnGetCursor = true;
        await File.AppendAllTextAsync(rollout, "{\"fault\":true}\n", Utf8WithoutBom);

        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("unexpected cursor failure", exception.Message);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await scheduler.DisposeAsync().AsTask());
        await scheduler.DisposeAsync();
        Assert.IsTrue(scheduler.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task WatcherCompensation_ProbeDiagnosticFaultsMonitor()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(codexHome, "sessions", "rollout-probe-diagnostic.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var context = new CollectorContext(directory.Path, TimeProvider.System);
        SourceProbeResult initial = await collector.ProbeAsync(
            context,
            CancellationToken.None);
        var monitor = new SourceChangeMonitor(
            collector,
            context,
            new CursorWriter(),
            new CollectionRequestQueue());
        await monitor.StartAsync(initial.Instances, CancellationToken.None);
        collector.ReportProbeDiagnostic = true;
        FileSystemWatcher watcher = Assert.ContainsSingle(Watchers(monitor).Keys);

        InvokeWatcherError(monitor, watcher);

        SourceProbeIncompleteException exception =
            await Assert.ThrowsExactlyAsync<SourceProbeIncompleteException>(async () =>
                await monitor.Completion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, exception.DiagnosticCount);
        await monitor.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeAsync_WithRapidAuditIsBoundedAndRepeatable()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(codexHome, "sessions", "rollout-audit-race.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var scheduler = new CollectionScheduler(
            collector,
            new CursorWriter(),
            new CollectorContext(directory.Path, TimeProvider.System),
            auditInterval: TimeSpan.FromMilliseconds(5));
        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => collector.CollectionCount >= 1);
        await Task.Delay(25);

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await scheduler.DisposeAsync();

        Assert.IsTrue(scheduler.Completion.IsCompleted);
    }

    [TestMethod]
    public async Task Scheduler_SynchronizesChangedSessionNamesWithoutAuditOrRepeatedWrites()
    {
        Assert.AreEqual(
            TimeSpan.FromMinutes(1),
            CollectionScheduler.DefaultSessionNameSyncInterval);
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        var collector = new SessionNameCollector(
            codexHome,
            "初始名称",
            new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        var writer = new CursorWriter();
        var scheduler = new CollectionScheduler(
            collector,
            writer,
            new CollectorContext(directory.Path, TimeProvider.System),
            auditInterval: TimeSpan.FromHours(1),
            sessionNameSyncInterval: TimeSpan.FromMilliseconds(25));

        await scheduler.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, writer.SessionNameSynchronizationCount);
        await Task.Delay(100);
        Assert.AreEqual(
            1,
            writer.SessionNameSynchronizationCount,
            "Unchanged source names must not cause periodic database writes.");

        collector.SetName(
            "用户改名",
            new DateTimeOffset(2026, 7, 31, 12, 1, 0, TimeSpan.Zero));
        await WaitUntilAsync(() => writer.SessionNameSynchronizationCount == 2);
        await Task.Delay(100);

        Assert.AreEqual(2, writer.SessionNameSynchronizationCount);
        UsageSessionNameMetadata synchronized =
            Assert.ContainsSingle(writer.LastSessionNames);
        Assert.AreEqual("session-name-test", synchronized.SessionId);
        Assert.AreEqual("用户改名", synchronized.SessionName);
        await scheduler.DisposeAsync();
    }

    [TestMethod]
    public async Task WatcherError_ReplacesOnlyFaultedRootWatcher()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string sessions = Path.Combine(codexHome, "sessions");
        string archive = Path.Combine(codexHome, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string rollout = Path.Combine(sessions, "rollout-error-recovery.jsonl");
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var monitor = new SourceChangeMonitor(
            collector,
            new CollectorContext(directory.Path, TimeProvider.System),
            new CursorWriter(),
            new CollectionRequestQueue());
        SourceProbeResult probe = await collector.ProbeAsync(
            new CollectorContext(directory.Path, TimeProvider.System),
            CancellationToken.None);
        await monitor.StartAsync(probe.Instances, CancellationToken.None);
        Dictionary<FileSystemWatcher, SourceInstanceDescriptor> watchers =
            Watchers(monitor);
        FileSystemWatcher failed = watchers.Keys.Single(value =>
            string.Equals(value.Path, sessions, StringComparison.OrdinalIgnoreCase));
        FileSystemWatcher healthy = watchers.Keys.Single(value =>
            string.Equals(value.Path, archive, StringComparison.OrdinalIgnoreCase));
        MethodInfo errorHandler = typeof(SourceChangeMonitor).GetMethod(
            "OnWatcherError",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        errorHandler.Invoke(
            monitor,
            [failed, new ErrorEventArgs(new InternalBufferOverflowException())]);
        await WaitUntilAsync(() =>
        {
            Dictionary<FileSystemWatcher, SourceInstanceDescriptor> current = Watchers(monitor);
            return current.Count == 2 &&
                !current.ContainsKey(failed) &&
                current.ContainsKey(healthy) &&
                current.Keys.Any(value => string.Equals(
                    value.Path,
                    sessions,
                    StringComparison.OrdinalIgnoreCase));
        });

        await monitor.DisposeAsync();
    }

    [TestMethod]
    public async Task WatcherErrors_ForBothRootsStartIndependentCompensations()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string sessions = Path.Combine(codexHome, "sessions");
        string archive = Path.Combine(codexHome, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        var collector = new BlockingProbeCollector(codexHome);
        var context = new CollectorContext(directory.Path, TimeProvider.System);
        SourceProbeResult probe = await collector.ProbeAsync(context, CancellationToken.None);
        var monitor = new SourceChangeMonitor(
            collector,
            context,
            new CursorWriter(),
            new CollectionRequestQueue());
        await monitor.StartAsync(probe.Instances, CancellationToken.None);
        Dictionary<FileSystemWatcher, SourceInstanceDescriptor> watchers = Watchers(monitor);
        collector.BeginBlockingCompensations();

        InvokeWatcherError(monitor, watchers.Keys.Single(value =>
            string.Equals(value.Path, sessions, StringComparison.OrdinalIgnoreCase)));
        await WaitUntilAsync(() => collector.CompensationProbeCount == 1);
        InvokeWatcherError(monitor, watchers.Keys.Single(value =>
            string.Equals(value.Path, archive, StringComparison.OrdinalIgnoreCase)));
        bool bothStarted = await WaitUntilOrTimeoutAsync(
            () => collector.CompensationProbeCount == 2,
            TimeSpan.FromMilliseconds(500));
        collector.ReleaseCompensations();
        await WaitUntilAsync(() => Watchers(monitor).Count == 2);
        await monitor.DisposeAsync();

        Assert.IsTrue(bothStarted);
    }

    [TestMethod]
    public async Task WatcherError_DuringRebuildSchedulesAnotherCompensation()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string sessions = Path.Combine(codexHome, "sessions");
        string archive = Path.Combine(codexHome, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string rollout = Path.Combine(sessions, "rollout-reerror.jsonl");
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        var collector = new CountingCollector(codexHome, rollout);
        var context = new CollectorContext(directory.Path, TimeProvider.System);
        SourceProbeResult probe = await collector.ProbeAsync(context, CancellationToken.None);
        var monitor = new SourceChangeMonitor(
            collector,
            context,
            new CursorWriter(),
            new CollectionRequestQueue());
        await monitor.StartAsync(probe.Instances, CancellationToken.None);
        Dictionary<FileSystemWatcher, SourceInstanceDescriptor> initial = Watchers(monitor);
        FileSystemWatcher failed = initial.Keys.Single(value =>
            string.Equals(value.Path, sessions, StringComparison.OrdinalIgnoreCase));
        FileSystemWatcher healthy = initial.Keys.Single(value =>
            string.Equals(value.Path, archive, StringComparison.OrdinalIgnoreCase));
        int reinjected = 0;
        SetWatcherEnabledHook(monitor, watcher =>
        {
            if (string.Equals(watcher.Path, sessions, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref reinjected, 1) == 0)
            {
                InvokeWatcherError(monitor, watcher);
            }
        });

        InvokeWatcherError(monitor, failed);
        await WaitUntilAsync(() =>
        {
            Dictionary<FileSystemWatcher, SourceInstanceDescriptor> current = Watchers(monitor);
            return collector.ProbeCount >= 3 &&
                current.Count == 2 &&
                current.ContainsKey(healthy) &&
                current.Keys.Any(value => string.Equals(
                    value.Path,
                    sessions,
                    StringComparison.OrdinalIgnoreCase));
        });
        await monitor.DisposeAsync();

        Assert.AreEqual(1, reinjected);
    }

    [TestMethod]
    public async Task Audit_UnchangedLockedEntityNeedsNoContentRead()
    {
        using var directory = new TestTempDirectory();
        string codexHome = directory.File(".codex");
        string rollout = Path.Combine(codexHome, "sessions", "rollout-metadata-only.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        await File.WriteAllTextAsync(rollout, "{}\n", Utf8WithoutBom);
        File.SetLastWriteTimeUtc(rollout, DateTime.UtcNow.AddMinutes(-2));
        string normalizedHome = CodexSourceIdentity.NormalizePath(codexHome);
        var instance = new SourceInstanceDescriptor(
            CodexSourceIdentity.InstanceId(normalizedHome),
            "codex",
            SourceKind.Jsonl,
            "Codex (Windows)",
            normalizedHome);
        var entity = new SourceEntityDescriptor(
            instance.SourceInstanceId,
            CodexSourceIdentity.EntityId(rollout),
            CodexSourceIdentity.NormalizePath(rollout));
        long length = new FileInfo(rollout).Length;
        string fingerprint = new string('0', 64);
        string cursorJson = new CodexCursor(
            new AgenTally.Core.Collectors.Jsonl.JsonlCursor(
                length,
                string.Empty,
                1,
                fingerprint),
            new CodexParseState()).Serialize();
        var cursor = new StoredCursor(
            instance.SourceInstanceId,
            entity.SourceEntityId,
            entity.SourcePath,
            cursorJson,
            fingerprint,
            CodexRolloutParser.CurrentParserVersion,
            DateTimeOffset.UtcNow,
            null,
            null);
        await using var exclusive = new FileStream(
            rollout,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        MethodInfo method = typeof(CollectionScheduler).GetMethod(
            "NeedsAudit",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        bool needsAudit = (bool)method.Invoke(null, [entity, cursor])!;

        Assert.IsFalse(needsAudit);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<bool> WaitUntilOrTimeoutAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    private static void InvokeWatcherError(
        SourceChangeMonitor monitor,
        FileSystemWatcher watcher)
    {
        MethodInfo method = typeof(SourceChangeMonitor).GetMethod(
            "OnWatcherError",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(
            monitor,
            [watcher, new ErrorEventArgs(new InternalBufferOverflowException())]);
    }

    private static void SetWatcherEnabledHook(
        SourceChangeMonitor monitor,
        Action<FileSystemWatcher> hook)
    {
        PropertyInfo property = typeof(SourceChangeMonitor).GetProperty(
            "WatcherEnabledTestHook",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        property.SetValue(monitor, hook);
    }

    private static Dictionary<FileSystemWatcher, SourceInstanceDescriptor> Watchers(
        SourceChangeMonitor monitor)
    {
        FieldInfo field = typeof(SourceChangeMonitor).GetField(
            "_watchers",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo gateField = typeof(SourceChangeMonitor).GetField(
            "_gate",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object gate = gateField.GetValue(monitor)!;
        lock (gate)
        {
            var current =
                (Dictionary<FileSystemWatcher, SourceInstanceDescriptor>)field.GetValue(monitor)!;
            return new Dictionary<FileSystemWatcher, SourceInstanceDescriptor>(current);
        }
    }

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private sealed class CountingCollector : IAgentCollector
    {
        private readonly SourceInstanceDescriptor _instance;
        private readonly SourceEntityDescriptor _entity;
        private int _collectionCount;
        private int _probeCount;

        public CountingCollector(string codexHome, string rollout)
        {
            string normalizedHome = CodexSourceIdentity.NormalizePath(codexHome);
            _instance = new SourceInstanceDescriptor(
                CodexSourceIdentity.InstanceId(normalizedHome),
                "codex",
                SourceKind.Jsonl,
                "Codex (Windows)",
                normalizedHome);
            _entity = new SourceEntityDescriptor(
                _instance.SourceInstanceId,
                CodexSourceIdentity.EntityId(rollout),
                CodexSourceIdentity.NormalizePath(rollout));
        }

        public string AgentId => "codex";

        public int CollectionCount => Volatile.Read(ref _collectionCount);

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public bool ReportProbeDiagnostic { get; set; }

        public List<StoredCursor?> SeenCursors { get; } = [];

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _probeCount);
            IReadOnlyList<SourceEntityDescriptor> entities = File.Exists(_entity.SourcePath)
                ? [_entity]
                : [];
            return ValueTask.FromResult(new SourceProbeResult(
                [_instance],
                entities,
                ReportProbeDiagnostic
                    ? [new CollectorDiagnostic(
                        "fixture.probe_incomplete",
                        "Fixture probe was incomplete.")]
                    : []));
        }

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int call = Interlocked.Increment(ref _collectionCount);
            lock (SeenCursors)
            {
                SeenCursors.Add(request.Cursor);
            }

            await Task.Yield();
            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                [],
                $"cursor-{call}",
                "fixture-fingerprint",
                "fixture-parser",
                []);
        }
    }

    private sealed class SessionNameCollector :
        IAgentCollector,
        IUsageSessionNameSource
    {
        private readonly object _gate = new();
        private readonly SourceInstanceDescriptor _instance;
        private IReadOnlyList<UsageSessionNameMetadata> _names;

        public SessionNameCollector(
            string codexHome,
            string name,
            DateTimeOffset updatedAtUtc)
        {
            string normalizedHome = CodexSourceIdentity.NormalizePath(codexHome);
            _instance = new SourceInstanceDescriptor(
                CodexSourceIdentity.InstanceId(normalizedHome),
                "codex",
                SourceKind.Jsonl,
                "Codex (Windows)",
                normalizedHome);
            _names = [new UsageSessionNameMetadata(
                "session-name-test",
                name,
                updatedAtUtc)];
        }

        public string AgentId => "codex";

        public void SetName(string name, DateTimeOffset updatedAtUtc)
        {
            lock (_gate)
            {
                _names = [new UsageSessionNameMetadata(
                    "session-name-test",
                    name,
                    updatedAtUtc)];
            }
        }

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SourceProbeResult(
                [_instance],
                [],
                []));
        }

        public Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_names);
            }
        }

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BlockingProbeCollector : IAgentCollector
    {
        private readonly SourceInstanceDescriptor _instance;
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blockCompensations;
        private int _compensationProbeCount;

        public BlockingProbeCollector(string codexHome)
        {
            string normalized = CodexSourceIdentity.NormalizePath(codexHome);
            _instance = new SourceInstanceDescriptor(
                CodexSourceIdentity.InstanceId(normalized),
                "codex",
                SourceKind.Jsonl,
                "Codex (Windows)",
                normalized);
        }

        public string AgentId => "codex";

        public int CompensationProbeCount =>
            Volatile.Read(ref _compensationProbeCount);

        public void BeginBlockingCompensations() =>
            Volatile.Write(ref _blockCompensations, 1);

        public void ReleaseCompensations() => _release.TrySetResult();

        public async ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _blockCompensations) == 1)
            {
                Interlocked.Increment(ref _compensationProbeCount);
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new SourceProbeResult([_instance], [], []);
        }

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CursorWriter : IUsageWriter
    {
        private readonly object _gate = new();
        private StoredCursor? _cursor;
        private int _commitCount;
        private int _appliedEventCount;
        private int _sessionNameSynchronizationCount;
        private IReadOnlyList<UsageSessionNameMetadata> _lastSessionNames = [];

        public int CommitCount => Volatile.Read(ref _commitCount);

        public int AppliedEventCount => Volatile.Read(ref _appliedEventCount);

        public int SessionNameSynchronizationCount =>
            Volatile.Read(ref _sessionNameSynchronizationCount);

        public IReadOnlyList<UsageSessionNameMetadata> LastSessionNames
        {
            get
            {
                lock (_gate)
                {
                    return _lastSessionNames;
                }
            }
        }

        public bool ThrowOnGetCursor { get; set; }

        public bool RequiresRebuild { get; set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<StoredCursor?> GetCursorAsync(
            string sourceInstanceId,
            string sourceEntityId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnGetCursor)
            {
                throw new InvalidOperationException("unexpected cursor failure");
            }

            lock (_gate)
            {
                return Task.FromResult(_cursor);
            }
        }

        public Task<SourceInstanceParserState> GetSourceInstanceParserStateAsync(
            SourceInstanceDescriptor instance,
            string requiredParserVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceInstanceParserState(
                HasDerivedData: _cursor is not null,
                RequiresRebuild: RequiresRebuild ||
                    (_cursor is not null &&
                     !string.Equals(
                         _cursor.ParserVersion,
                         requiredParserVersion,
                         StringComparison.Ordinal))));
        }

        public Task<WriteResult> CommitAsync(
            UsageEventBatch batch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _cursor = new StoredCursor(
                    batch.Instance.SourceInstanceId,
                    batch.Entity.SourceEntityId,
                    batch.Entity.SourcePath,
                    batch.CursorJson,
                    batch.SourceFingerprint,
                    batch.ParserVersion,
                    batch.CheckedAtUtc,
                    null,
                    null);
            }

            Interlocked.Increment(ref _commitCount);
            Interlocked.Add(ref _appliedEventCount, batch.Events.Count);
            return Task.FromResult(new WriteResult(batch.Events.Count, 0));
        }

        public Task SynchronizeSessionNamesAsync(
            SourceInstanceDescriptor instance,
            IReadOnlyList<UsageSessionNameMetadata> sessionNames,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _lastSessionNames = sessionNames.ToArray();
            }

            Interlocked.Increment(ref _sessionNameSynchronizationCount);
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            SourceInstanceDescriptor instance,
            SourceEntityDescriptor entity,
            string error,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StalledBatchLimitCollector : IAgentCollector
    {
        private readonly SourceInstanceDescriptor _instance;
        private readonly SourceEntityDescriptor _entity;
        private int _collectionCount;

        public StalledBatchLimitCollector(string codexHome)
        {
            string normalized = CodexSourceIdentity.NormalizePath(codexHome);
            _instance = new SourceInstanceDescriptor(
                CodexSourceIdentity.InstanceId(normalized),
                "codex",
                SourceKind.Jsonl,
                "Codex (Windows)",
                normalized);
            _entity = new SourceEntityDescriptor(
                _instance.SourceInstanceId,
                "codex:rollout:stalled",
                Path.Combine(normalized, "sessions", "rollout-stalled.jsonl"));
        }

        public string AgentId => "codex";

        public int CollectionCount => Volatile.Read(ref _collectionCount);

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SourceProbeResult(
                [_instance],
                [_entity],
                []));
        }

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _collectionCount);
            await Task.Yield();
            yield return new CollectedBatch(
                request.Instance,
                request.Entity,
                [],
                "stalled-cursor",
                "fixture-fingerprint",
                CodexRolloutParser.CurrentParserVersion,
                [new CollectorDiagnostic(
                    "collector.batch_limit_reached",
                    "Collection stopped at its bounded batch limit.",
                    request.Entity.SourceEntityId)]);
        }
    }

    private sealed class EarlyFailingCollector : IAgentCollector
    {
        private readonly SourceInstanceDescriptor _instance;
        private readonly IReadOnlyList<SourceEntityDescriptor> _entities;

        public EarlyFailingCollector(string codexHome, int entityCount)
        {
            string normalized = CodexSourceIdentity.NormalizePath(codexHome);
            _instance = new SourceInstanceDescriptor(
                CodexSourceIdentity.InstanceId(normalized),
                "codex",
                SourceKind.Jsonl,
                "Codex (Windows)",
                normalized);
            _entities = Enumerable.Range(0, entityCount)
                .Select(index => new SourceEntityDescriptor(
                    _instance.SourceInstanceId,
                    $"entity-{index}",
                    Path.Combine(normalized, "sessions", $"rollout-{index}.jsonl")))
                .ToArray();
        }

        public string AgentId => "codex";

        public ValueTask<SourceProbeResult> ProbeAsync(
            CollectorContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SourceProbeResult([_instance], _entities, []));

        public async IAsyncEnumerable<CollectedBatch> CollectAsync(
            CollectionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("early sync failure");
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
