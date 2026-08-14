using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenTally.Storage.Database;
using AgenTally.Storage.Runtime;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Backup;

public enum RestoreFaultStage
{
    Unpack = 1,
    Validate = 2,
    Migrate = 3,
    AcquireLease = 4,
    FinalSwitch = 5,
    RestartCore = 6
}

public sealed record BackupManifest(
    int FormatVersion,
    int DatabaseSchemaVersion,
    string ApplicationVersion,
    AgenTallyChannel SourceChannel,
    DateTimeOffset CreatedAtUtc,
    string DatabaseSha256,
    IReadOnlyDictionary<string, long> KeyTableCounts);

public sealed record BackupCreationResult(
    string BackupPath,
    BackupManifest Manifest);

public sealed class StagedBackupRestore : IDisposable
{
    private int _disposed;

    internal StagedBackupRestore(string databasePath, BackupManifest manifest)
    {
        DatabasePath = databasePath;
        Manifest = manifest;
    }

    public string DatabasePath { get; }

    public BackupManifest Manifest { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DatabaseBackupArchive.DeleteDatabaseFiles(DatabasePath);
        }
    }
}

public sealed class DatabaseBackupArchive
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";
    public const string DatabaseEntryName = "database.sqlite";
    private const long MaximumManifestBytes = 64 * 1024;
    private const long MaximumDatabaseBytes = 256L * 1024 * 1024 * 1024;
    private static readonly string[] KeyTables =
        ["usage_events", "source_instances", "pricing_overrides"];
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public async Task<BackupCreationResult> CreateAsync(
        string sourceDatabasePath,
        string destinationBackupPath,
        string workingDirectory,
        AgenTallyChannel channel,
        string applicationVersion,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationBackupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        string source = Path.GetFullPath(sourceDatabasePath);
        string destination = Path.GetFullPath(destinationBackupPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The AgenTally database does not exist.", source);
        }
        if (File.Exists(destination))
        {
            throw new IOException("The selected backup file already exists.");
        }

        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException(
                "The selected backup path has no parent directory.");
        }

        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(Path.GetFullPath(workingDirectory));
        string snapshotPath = Path.Combine(
            Path.GetFullPath(workingDirectory),
            $"backup-snapshot-{Guid.NewGuid():N}.sqlite");
        string temporaryArchive = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CreateConsistentSnapshotAsync(source, snapshotPath, cancellationToken);
            DatabaseValidation validation = await ValidateDatabaseAsync(
                snapshotPath,
                migrate: false,
                cancellationToken);
            string hash = await ComputeSha256Async(snapshotPath, cancellationToken);
            var manifest = new BackupManifest(
                CurrentFormatVersion,
                validation.SchemaVersion,
                applicationVersion,
                channel,
                createdAtUtc.ToUniversalTime(),
                hash,
                validation.KeyTableCounts);
            await WriteArchiveAsync(
                temporaryArchive,
                snapshotPath,
                manifest,
                cancellationToken);
            await ValidateArchiveAsync(
                temporaryArchive,
                channel,
                workingDirectory,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryArchive, destination, overwrite: false);
            return new BackupCreationResult(destination, manifest);
        }
        finally
        {
            DeleteDatabaseFiles(snapshotPath);
            TryDelete(temporaryArchive);
        }
    }

    public async Task<StagedBackupRestore> StageRestoreAsync(
        string backupPath,
        string currentDatabasePath,
        AgenTallyChannel expectedChannel,
        Func<RestoreFaultStage, CancellationToken, Task>? faultInjection,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDatabasePath);
        string archivePath = Path.GetFullPath(backupPath);
        string databasePath = Path.GetFullPath(currentDatabasePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                "The selected AgenTally backup does not exist.",
                archivePath);
        }

        string directory = Path.GetDirectoryName(databasePath) ??
            throw new InvalidOperationException(
                "The current database has no parent directory.");
        Directory.CreateDirectory(directory);
        string stagingPath = Path.Combine(
            directory,
            $".{Path.GetFileName(databasePath)}.restore-{Guid.NewGuid():N}.tmp");
        try
        {
            await InjectAsync(faultInjection, RestoreFaultStage.Unpack, cancellationToken);
            BackupManifest manifest = await ExtractStrictAsync(
                archivePath,
                stagingPath,
                expectedChannel,
                cancellationToken);
            await InjectAsync(faultInjection, RestoreFaultStage.Validate, cancellationToken);
            string hash = await ComputeSha256Async(stagingPath, cancellationToken);
            if (!string.Equals(hash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The backup database hash does not match its manifest.");
            }

            await InjectAsync(faultInjection, RestoreFaultStage.Migrate, cancellationToken);
            DatabaseValidation validation = await ValidateDatabaseAsync(
                stagingPath,
                migrate: true,
                cancellationToken);
            AssertCounts(manifest.KeyTableCounts, validation.KeyTableCounts);
            return new StagedBackupRestore(stagingPath, manifest);
        }
        catch
        {
            DeleteDatabaseFiles(stagingPath);
            throw;
        }
    }

    public async Task CommitRestoreAsync(
        StagedBackupRestore staged,
        string currentDatabasePath,
        Func<RestoreFaultStage, CancellationToken, Task>? faultInjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDatabasePath);
        string current = Path.GetFullPath(currentDatabasePath);
        string staging = Path.GetFullPath(staged.DatabasePath);
        string directory = Path.GetDirectoryName(current) ??
            throw new InvalidOperationException(
                "The current database has no parent directory.");
        if (!string.Equals(directory, Path.GetDirectoryName(staging), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The staged database must be on the same volume and directory.");
        }

        string rollback = Path.Combine(
            directory,
            $".{Path.GetFileName(current)}.rollback-{Guid.NewGuid():N}.bak");
        bool currentExisted = File.Exists(current);
        bool switched = false;
        try
        {
            await InjectAsync(
                faultInjection,
                RestoreFaultStage.AcquireLease,
                cancellationToken);
            if (currentExisted)
            {
                await CheckpointCurrentDatabaseAsync(current, cancellationToken);
            }
            await InjectAsync(faultInjection, RestoreFaultStage.FinalSwitch, cancellationToken);
            DeleteSidecars(current);
            if (currentExisted)
            {
                File.Replace(staging, current, rollback, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(staging, current);
            }
            switched = true;
            DatabaseValidation validation = await ValidateDatabaseAsync(
                current,
                migrate: false,
                cancellationToken);
            AssertCounts(staged.Manifest.KeyTableCounts, validation.KeyTableCounts);
            await InjectAsync(
                faultInjection,
                RestoreFaultStage.RestartCore,
                cancellationToken);
            TryDelete(rollback);
        }
        catch
        {
            if (switched)
            {
                DeleteSidecars(current);
                if (currentExisted && File.Exists(rollback))
                {
                    File.Replace(rollback, current, null, ignoreMetadataErrors: true);
                }
                else if (!currentExisted)
                {
                    TryDelete(current);
                }
            }
            throw;
        }
        finally
        {
            TryDelete(rollback);
        }
    }

    public async Task<BackupManifest> ValidateArchiveAsync(
        string backupPath,
        AgenTallyChannel expectedChannel,
        CancellationToken cancellationToken) => await ValidateArchiveAsync(
            backupPath,
            expectedChannel,
            Path.GetTempPath(),
            cancellationToken);

    public async Task<BackupManifest> ValidateArchiveAsync(
        string backupPath,
        AgenTallyChannel expectedChannel,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string archive = Path.GetFullPath(backupPath);
        string workingRoot = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(workingRoot);
        string temporaryDirectory = Path.Combine(
            workingRoot,
            $"AgenTally-backup-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string database = Path.Combine(temporaryDirectory, "database.sqlite");
        try
        {
            BackupManifest manifest = await ExtractStrictAsync(
                archive,
                database,
                expectedChannel,
                cancellationToken);
            string hash = await ComputeSha256Async(database, cancellationToken);
            if (!string.Equals(hash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The backup database hash does not match its manifest.");
            }
            await ValidateDatabaseAsync(database, migrate: false, cancellationToken);
            return manifest;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task CreateConsistentSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        await using var source = new SqliteConnection(sourceBuilder.ConnectionString);
        await using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task WriteArchiveAsync(
        string archivePath,
        string databasePath,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        ZipArchiveEntry manifestEntry = archive.CreateEntry(
            ManifestEntryName,
            CompressionLevel.Optimal);
        await using (Stream entry = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                entry,
                manifest,
                SerializerOptions,
                cancellationToken);
        }

        ZipArchiveEntry databaseEntry = archive.CreateEntry(
            DatabaseEntryName,
            CompressionLevel.Optimal);
        await using (Stream entry = databaseEntry.Open())
        await using (var database = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await database.CopyToAsync(entry, cancellationToken);
        }
    }

    private static async Task<BackupManifest> ExtractStrictAsync(
        string archivePath,
        string databaseDestination,
        AgenTallyChannel expectedChannel,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count != 2)
        {
            throw new InvalidDataException(
                "A backup must contain exactly two declared entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;
            if (Path.IsPathFullyQualified(name) ||
                name.Contains("..", StringComparison.Ordinal) ||
                name.Contains('\\') ||
                (name != ManifestEntryName && name != DatabaseEntryName) ||
                !entries.TryAdd(name, entry))
            {
                throw new InvalidDataException(
                    "The backup contains an unknown, duplicate, or unsafe entry.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out ZipArchiveEntry? manifestEntry) ||
            !entries.TryGetValue(DatabaseEntryName, out ZipArchiveEntry? databaseEntry) ||
            manifestEntry.Length is < 1 or > MaximumManifestBytes ||
            databaseEntry.Length is < 1 or > MaximumDatabaseBytes)
        {
            throw new InvalidDataException(
                "The backup entries are missing or outside supported limits.");
        }

        BackupManifest manifest;
        await using (Stream entry = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                entry,
                SerializerOptions,
                cancellationToken) ?? throw new InvalidDataException(
                    "The backup manifest is empty.");
        }

        if (manifest.FormatVersion != CurrentFormatVersion ||
            manifest.DatabaseSchemaVersion is < 2 or > DatabaseSchemaInfo.CurrentVersion ||
            manifest.SourceChannel != expectedChannel ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(manifest.ApplicationVersion) ||
            manifest.DatabaseSha256.Length != 64 ||
            manifest.KeyTableCounts.Count != KeyTables.Length ||
            KeyTables.Any(table =>
                !manifest.KeyTableCounts.TryGetValue(table, out long value) || value < 0))
        {
            throw new InvalidDataException(
                "The backup manifest is unsupported or does not match this channel.");
        }

        await using (Stream entry = databaseEntry.Open())
        await using (var database = new FileStream(
            databaseDestination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await entry.CopyToAsync(database, cancellationToken);
            await database.FlushAsync(cancellationToken);
        }
        return manifest;
    }

    private static async Task<DatabaseValidation> ValidateDatabaseAsync(
        string databasePath,
        bool migrate,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = migrate ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        if (migrate)
        {
            await DatabaseSchema.InitializeAsync(connection, cancellationToken);
        }

        int schemaVersion = Convert.ToInt32(await ExecuteScalarAsync(
            connection,
            "PRAGMA user_version;",
            cancellationToken));
        if (schemaVersion is < 2 or > DatabaseSchemaInfo.CurrentVersion)
        {
            throw new InvalidDataException("The backup database schema is unsupported.");
        }

        string quickCheck = Convert.ToString(await ExecuteScalarAsync(
            connection,
            "PRAGMA quick_check;",
            cancellationToken)) ?? string.Empty;
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SQLite quick_check did not report ok.");
        }

        await using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            await using SqliteDataReader reader =
                await foreignKeys.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    "The backup database contains foreign-key violations.");
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in KeyTables)
        {
            counts[table] = Convert.ToInt64(await ExecuteScalarAsync(
                connection,
                $"SELECT COUNT(*) FROM {table};",
                cancellationToken));
        }

        if (migrate)
        {
            await using SqliteCommand checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            await using SqliteCommand journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode = DELETE;";
            await journal.ExecuteScalarAsync(cancellationToken);
        }
        return new DatabaseValidation(schemaVersion, counts);
    }

    private static async Task CheckpointCurrentDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode = DELETE;";
        await journal.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void AssertCounts(
        IReadOnlyDictionary<string, long> expected,
        IReadOnlyDictionary<string, long> actual)
    {
        foreach ((string table, long expectedCount) in expected)
        {
            if (!actual.TryGetValue(table, out long actualCount) || actualCount != expectedCount)
            {
                throw new InvalidDataException(
                    $"The backup table count for {table} does not match.");
            }
        }
    }

    private static Task InjectAsync(
        Func<RestoreFaultStage, CancellationToken, Task>? faultInjection,
        RestoreFaultStage stage,
        CancellationToken cancellationToken) =>
        faultInjection?.Invoke(stage, cancellationToken) ?? Task.CompletedTask;

    internal static void DeleteDatabaseFiles(string databasePath)
    {
        TryDelete(databasePath);
        DeleteSidecars(databasePath);
    }

    private static void DeleteSidecars(string databasePath)
    {
        TryDelete($"{databasePath}-wal");
        TryDelete($"{databasePath}-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record DatabaseValidation(
        int SchemaVersion,
        IReadOnlyDictionary<string, long> KeyTableCounts);
}
