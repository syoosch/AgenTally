using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Database;

public sealed class SqliteConnectionFactory
{
    public SqliteConnectionFactory(StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);

        DatabasePath = Path.GetFullPath(options.DatabasePath);
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenWriterAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 2,
            Pooling = false
        };

        return await OpenAsync(builder, cancellationToken);
    }

    public async Task<SqliteConnection> OpenReaderAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 2,
            Pooling = false
        };

        return await OpenAsync(builder, cancellationToken);
    }

    private static async Task<SqliteConnection> OpenAsync(
        SqliteConnectionStringBuilder builder,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(builder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ConfigureAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 2000;
            PRAGMA foreign_keys = ON;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
