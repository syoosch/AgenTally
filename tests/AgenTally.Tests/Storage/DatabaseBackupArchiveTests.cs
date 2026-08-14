using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using AgenTally.Storage;
using AgenTally.Storage.Backup;
using AgenTally.Storage.Database;
using AgenTally.Storage.Runtime;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class DatabaseBackupArchiveTests
{
    [TestMethod]
    public async Task CreateAndRestore_RoundTripsStrictTwoEntrySnapshot()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        string backupPath = directory.File("roundtrip.agentally-backup");
        await InitializeAsync(databasePath);
        await InsertSourceAsync(databasePath, "source-before");
        var archive = new DatabaseBackupArchive();

        BackupCreationResult created = await archive.CreateAsync(
            databasePath,
            backupPath,
            directory.File("work"),
            AgenTallyChannel.Development,
            "1.2.3-test",
            new DateTimeOffset(2026, 8, 11, 1, 2, 3, TimeSpan.Zero),
            CancellationToken.None);

        Assert.IsTrue(File.Exists(backupPath));
        Assert.AreEqual(DatabaseBackupArchive.CurrentFormatVersion, created.Manifest.FormatVersion);
        Assert.AreEqual(DatabaseSchemaInfo.CurrentVersion, created.Manifest.DatabaseSchemaVersion);
        Assert.AreEqual(1L, created.Manifest.KeyTableCounts["source_instances"]);
        using (var zip = ZipFile.OpenRead(backupPath))
        {
            CollectionAssert.AreEquivalent(
                new[]
                {
                    DatabaseBackupArchive.ManifestEntryName,
                    DatabaseBackupArchive.DatabaseEntryName
                },
                zip.Entries.Select(static entry => entry.FullName).ToArray());
        }

        await InsertSourceAsync(databasePath, "source-after");
        Assert.AreEqual(2L, await CountSourcesAsync(databasePath));
        using StagedBackupRestore staged = await archive.StageRestoreAsync(
            backupPath,
            databasePath,
            AgenTallyChannel.Development,
            faultInjection: null,
            CancellationToken.None);
        await archive.CommitRestoreAsync(
            staged,
            databasePath,
            faultInjection: null,
            CancellationToken.None);

        Assert.AreEqual(1L, await CountSourcesAsync(databasePath));
        BackupManifest validated = await archive.ValidateArchiveAsync(
            backupPath,
            AgenTallyChannel.Development,
            CancellationToken.None);
        Assert.AreEqual(created.Manifest.DatabaseSha256, validated.DatabaseSha256);
    }

    [TestMethod]
    public async Task StageRestore_RejectsUnknownOrUnsafeEntryBeforeChangingCurrentDatabase()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        string backupPath = directory.File("valid.agentally-backup");
        string unsafePath = directory.File("unsafe.agentally-backup");
        await InitializeAsync(databasePath);
        await InsertSourceAsync(databasePath, "source-before");
        var archive = new DatabaseBackupArchive();
        await archive.CreateAsync(
            databasePath,
            backupPath,
            directory.File("work"),
            AgenTallyChannel.Development,
            "test",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        CopyArchiveWithExtraEntry(backupPath, unsafePath, "../escaped.txt");
        string before = await Sha256Async(databasePath);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            archive.StageRestoreAsync(
                unsafePath,
                databasePath,
                AgenTallyChannel.Development,
                faultInjection: null,
                CancellationToken.None));

        Assert.AreEqual(before, await Sha256Async(databasePath));
        Assert.IsFalse(Directory.EnumerateFiles(directory.Path)
            .Any(static path => path.Contains(".restore-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task StageRestore_RejectsHashMismatchAndCrossChannelBackup()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        string backupPath = directory.File("valid.agentally-backup");
        await InitializeAsync(databasePath);
        var archive = new DatabaseBackupArchive();
        await archive.CreateAsync(
            databasePath,
            backupPath,
            directory.File("work"),
            AgenTallyChannel.Development,
            "test",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            archive.StageRestoreAsync(
                backupPath,
                databasePath,
                AgenTallyChannel.Stable,
                faultInjection: null,
                CancellationToken.None));

        string corrupted = directory.File("corrupted.agentally-backup");
        CopyArchiveWithCorruptedDatabase(backupPath, corrupted);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            archive.StageRestoreAsync(
                corrupted,
                databasePath,
                AgenTallyChannel.Development,
                faultInjection: null,
                CancellationToken.None));
    }

    [TestMethod]
    [DataRow(RestoreFaultStage.Unpack)]
    [DataRow(RestoreFaultStage.Validate)]
    [DataRow(RestoreFaultStage.Migrate)]
    [DataRow(RestoreFaultStage.AcquireLease)]
    [DataRow(RestoreFaultStage.FinalSwitch)]
    [DataRow(RestoreFaultStage.RestartCore)]
    public async Task RestoreFaultInjection_PreservesOrRollsBackOriginalDatabase(
        RestoreFaultStage failedStage)
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("agentally.db");
        string backupPath = directory.File("valid.agentally-backup");
        await InitializeAsync(databasePath);
        await InsertSourceAsync(databasePath, "backup-source");
        var archive = new DatabaseBackupArchive();
        await archive.CreateAsync(
            databasePath,
            backupPath,
            directory.File("work"),
            AgenTallyChannel.Development,
            "test",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await InsertSourceAsync(databasePath, "current-source");
        string before = await Sha256Async(databasePath);

        Task Inject(RestoreFaultStage stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return stage == failedStage
                ? Task.FromException(new IOException($"Injected {stage}."))
                : Task.CompletedTask;
        }

        if (failedStage <= RestoreFaultStage.Migrate)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                archive.StageRestoreAsync(
                    backupPath,
                    databasePath,
                    AgenTallyChannel.Development,
                    Inject,
                    CancellationToken.None));
        }
        else
        {
            using StagedBackupRestore staged = await archive.StageRestoreAsync(
                backupPath,
                databasePath,
                AgenTallyChannel.Development,
                faultInjection: null,
                CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() =>
                archive.CommitRestoreAsync(
                    staged,
                    databasePath,
                    Inject,
                    CancellationToken.None));
        }

        if (failedStage <= RestoreFaultStage.AcquireLease)
        {
            Assert.AreEqual(before, await Sha256Async(databasePath));
        }
        Assert.AreEqual(2L, await CountSourcesAsync(databasePath));
        CollectionAssert.AreEquivalent(
            new[] { "backup-source", "current-source" },
            (await ReadSourceIdsAsync(databasePath)).ToArray());
    }

    private static async Task InitializeAsync(string databasePath)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(databasePath));
        var writer = new SqliteUsageWriter(connections);
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

    private static async Task<IReadOnlyList<string>> ReadSourceIdsAsync(
        string databasePath)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(databasePath));
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT source_instance_id FROM source_instances ORDER BY source_instance_id;";
        var result = new List<string>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    private static void CopyArchiveWithExtraEntry(
        string source,
        string destination,
        string extraName)
    {
        using ZipArchive input = ZipFile.OpenRead(source);
        using ZipArchive output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (ZipArchiveEntry entry in input.Entries)
        {
            ZipArchiveEntry copy = output.CreateEntry(entry.FullName);
            using Stream sourceStream = entry.Open();
            using Stream destinationStream = copy.Open();
            sourceStream.CopyTo(destinationStream);
        }
        output.CreateEntry(extraName);
    }

    private static void CopyArchiveWithCorruptedDatabase(
        string source,
        string destination)
    {
        using ZipArchive input = ZipFile.OpenRead(source);
        using ZipArchive output = ZipFile.Open(destination, ZipArchiveMode.Create);
        foreach (ZipArchiveEntry entry in input.Entries)
        {
            ZipArchiveEntry copy = output.CreateEntry(entry.FullName);
            using Stream sourceStream = entry.Open();
            using Stream destinationStream = copy.Open();
            sourceStream.CopyTo(destinationStream);
            if (entry.FullName == DatabaseBackupArchive.DatabaseEntryName)
            {
                destinationStream.WriteByte(0x5A);
            }
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, CancellationToken.None));
    }
}
