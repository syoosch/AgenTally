using System.IO;
using AgenTally.Core.Hosting;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Runtime;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Core;

[TestClass]
[DoNotParallelize]
public sealed class CoreDataMaintenanceTests
{
    [TestMethod]
    public async Task ManagedCore_CreateAndRestoreBackupRoundTrip()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        await InitializeAsync(profile.DatabasePath);
        await InsertSourceAsync(profile.DatabasePath, "backed-up");
        string backupPath = directory.File("manual.agentally-backup");
        var requests = new DataMaintenanceRequestStore(profile);
        var output = new StringWriter();
        var host = new CoreHost(
            new StorageOptions(profile.DatabasePath),
            output: output,
            runtimeProfile: profile);
        await requests.WriteAsync(
            DataMaintenanceOperation.CreateBackup,
            backupPath,
            CancellationToken.None);

        int backupExit = await host.RunAsync(
            ["--create-backup"],
            CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.Success, backupExit);
        Assert.IsTrue(File.Exists(backupPath));
        Assert.IsFalse(File.Exists(profile.DataMaintenanceRequestPath));
        await InsertSourceAsync(profile.DatabasePath, "newer-current");
        Assert.AreEqual(2L, await CountSourcesAsync(profile.DatabasePath));
        await requests.WriteAsync(
            DataMaintenanceOperation.RestoreBackup,
            backupPath,
            CancellationToken.None);

        int restoreExit = await host.RunAsync(
            ["--restore-backup"],
            CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.Success, restoreExit);
        Assert.AreEqual(1L, await CountSourcesAsync(profile.DatabasePath));
        Assert.IsFalse(File.Exists(profile.DataMaintenanceRequestPath));
        StringAssert.Contains(output.ToString(), "完整性校验");
    }

    [TestMethod]
    public async Task ManagedCore_RejectsMismatchedRequestWithoutChangingDatabase()
    {
        using var directory = new TestTempDirectory();
        AgenTallyRuntimeProfile profile = CreateProfile(directory);
        await InitializeAsync(profile.DatabasePath);
        await InsertSourceAsync(profile.DatabasePath, "current");
        Directory.CreateDirectory(profile.RuntimeRoot);
        await File.WriteAllTextAsync(
            profile.DataMaintenanceRequestPath,
            "{\"protocolVersion\":1,\"channel\":\"Stable\",\"profileId\":\"other\",\"operation\":\"RestoreBackup\",\"backupPath\":\"C:\\\\missing.agentally-backup\",\"requestedAtUtc\":\"2026-08-11T00:00:00Z\"}");
        var host = new CoreHost(
            new StorageOptions(profile.DatabasePath),
            output: new StringWriter(),
            runtimeProfile: profile);

        int exit = await host.RunAsync(
            ["--restore-backup"],
            CancellationToken.None);

        Assert.AreEqual(CoreExitCodes.RuntimeFailure, exit);
        Assert.AreEqual(1L, await CountSourcesAsync(profile.DatabasePath));
        Assert.IsFalse(File.Exists(profile.DataMaintenanceRequestPath));
    }

    private static AgenTallyRuntimeProfile CreateProfile(
        TestTempDirectory directory)
    {
        string app = directory.File("app");
        string local = directory.File("local");
        string user = directory.File("user");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(user);
        return AgenTallyRuntimeProfile.CreateStable(app, local, user);
    }

    private static async Task InitializeAsync(string databasePath)
    {
        var writer = new SqliteUsageWriter(
            new SqliteConnectionFactory(new StorageOptions(databasePath)));
        await writer.InitializeAsync(CancellationToken.None);
    }

    private static async Task InsertSourceAsync(
        string databasePath,
        string sourceId)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(databasePath));
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
                last_checked_unix_ms
            ) VALUES ($id, 'test-agent', 'test-kind', $id, $path, 1);
            """;
        command.Parameters.AddWithValue("$id", sourceId);
        command.Parameters.AddWithValue("$path", $"C:\\test\\{sourceId}");
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<long> CountSourcesAsync(string databasePath)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(databasePath));
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_instances;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
