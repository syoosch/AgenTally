using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class SqliteUsageDataChangeMonitorTests
{
    [TestMethod]
    public async Task ObserveAsync_CoalescesExternalCommitsOnOneReaderConnection()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        using var monitor = new SqliteUsageDataChangeMonitor(connections);

        Assert.AreEqual(
            UsageDataChangeState.Changed,
            await monitor.ObserveAsync(CancellationToken.None));
        Assert.AreEqual(
            UsageDataChangeState.Unchanged,
            await monitor.ObserveAsync(CancellationToken.None));

        await InsertSourceAsync(connections, "first");
        await InsertSourceAsync(connections, "second");

        Assert.AreEqual(
            UsageDataChangeState.Changed,
            await monitor.ObserveAsync(CancellationToken.None));
        Assert.AreEqual(
            UsageDataChangeState.Unchanged,
            await monitor.ObserveAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ObserveAsync_ReconnectsConservativelyAfterDatabaseAppears()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));
        using var monitor = new SqliteUsageDataChangeMonitor(connections);

        Assert.AreEqual(
            UsageDataChangeState.Unavailable,
            await monitor.ObserveAsync(CancellationToken.None));

        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(
            UsageDataChangeState.Changed,
            await monitor.ObserveAsync(CancellationToken.None));
        Assert.AreEqual(
            UsageDataChangeState.Unchanged,
            await monitor.ObserveAsync(CancellationToken.None));

        monitor.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await monitor.ObserveAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ResetAsync_ClosesReaderAndMakesNextObservationConservative()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("agentally.db")));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        using var monitor = new SqliteUsageDataChangeMonitor(connections);
        Assert.AreEqual(
            UsageDataChangeState.Changed,
            await monitor.ObserveAsync(CancellationToken.None));
        Assert.AreEqual(
            UsageDataChangeState.Unchanged,
            await monitor.ObserveAsync(CancellationToken.None));

        await monitor.ResetAsync(CancellationToken.None);

        Assert.AreEqual(
            UsageDataChangeState.Changed,
            await monitor.ObserveAsync(CancellationToken.None));
    }

    private static async Task InsertSourceAsync(
        SqliteConnectionFactory connections,
        string suffix)
    {
        await using SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_instances (
                source_instance_id,
                agent_id,
                source_kind,
                display_name,
                root_path,
                last_checked_unix_ms)
            VALUES (
                $source_instance_id,
                'codex',
                0,
                'Codex',
                'test-root',
                0);
            """;
        command.Parameters.AddWithValue("$source_instance_id", $"source-{suffix}");
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
