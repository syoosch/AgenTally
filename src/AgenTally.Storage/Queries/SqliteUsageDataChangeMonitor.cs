using AgenTally.Storage.Database;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Queries;

public enum UsageDataChangeState
{
    Changed,
    Unchanged,
    Unavailable
}

public interface IUsageDataChangeMonitor : IDisposable
{
    Task<UsageDataChangeState> ObserveAsync(CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SqliteUsageDataChangeMonitor : IUsageDataChangeMonitor
{
    private readonly SqliteConnectionFactory _connections;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private long? _lastDataVersion;
    private int _disposed;

    public SqliteUsageDataChangeMonitor(SqliteConnectionFactory connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public async Task<UsageDataChangeState> ObserveAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            try
            {
                _connection ??= await OpenConnectionAsync(cancellationToken);
                await using SqliteCommand command = _connection.CreateCommand();
                command.CommandText = "PRAGMA main.data_version;";
                object? value = await command.ExecuteScalarAsync(cancellationToken);
                long currentDataVersion = Convert.ToInt64(value);
                UsageDataChangeState result = !_lastDataVersion.HasValue ||
                    _lastDataVersion.Value != currentDataVersion
                    ? UsageDataChangeState.Changed
                    : UsageDataChangeState.Unchanged;
                _lastDataVersion = currentDataVersion;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                ResetConnection();
                return UsageDataChangeState.Unavailable;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ResetConnection();
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            ResetConnection();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA query_only = ON;
                PRAGMA cache_size = -64;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void ResetConnection()
    {
        SqliteConnection? connection = Interlocked.Exchange(ref _connection, null);
        _lastDataVersion = null;
        connection?.Dispose();
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException;
}
