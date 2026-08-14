using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Storage.Runtime;

public enum DataMaintenanceOperation
{
    CreateBackup = 1,
    RestoreBackup = 2
}

public sealed record DataMaintenanceRequest(
    int ProtocolVersion,
    AgenTallyChannel Channel,
    string ProfileId,
    DataMaintenanceOperation Operation,
    string BackupPath,
    DateTimeOffset RequestedAtUtc)
{
    public const int CurrentProtocolVersion = 1;
}

public sealed class DataMaintenanceRequestStore
{
    private const int MaximumRequestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private readonly AgenTallyRuntimeProfile _profile;

    public DataMaintenanceRequestStore(AgenTallyRuntimeProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public async Task WriteAsync(
        DataMaintenanceOperation operation,
        string backupPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        string fullPath = Path.GetFullPath(backupPath);
        var request = new DataMaintenanceRequest(
            DataMaintenanceRequest.CurrentProtocolVersion,
            _profile.Channel,
            _profile.ProfileId,
            operation,
            fullPath,
            DateTimeOffset.UtcNow);
        string destination = _profile.DataMaintenanceRequestPath;
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The data-maintenance request path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    request,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<DataMaintenanceRequest> ReadAsync(
        DataMaintenanceOperation expectedOperation,
        CancellationToken cancellationToken)
    {
        string path = _profile.DataMaintenanceRequestPath;
        byte[] payload = await BoundedFileReader.ReadAllBytesAsync(
            path,
            MaximumRequestBytes,
            cancellationToken);
        DataMaintenanceRequest request;
        try
        {
            request = JsonSerializer.Deserialize<DataMaintenanceRequest>(
                payload,
                SerializerOptions) ?? throw new InvalidDataException(
                    "The data-maintenance request is empty.");
        }
        finally
        {
            Array.Clear(payload);
        }
        if (request.ProtocolVersion != DataMaintenanceRequest.CurrentProtocolVersion ||
            request.Channel != _profile.Channel ||
            !string.Equals(request.ProfileId, _profile.ProfileId, StringComparison.Ordinal) ||
            request.Operation != expectedOperation ||
            string.IsNullOrWhiteSpace(request.BackupPath) ||
            !Path.IsPathFullyQualified(request.BackupPath) ||
            request.RequestedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
            request.RequestedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-30))
        {
            throw new InvalidDataException(
                "The data-maintenance request does not match this runtime profile.");
        }

        return request with { BackupPath = Path.GetFullPath(request.BackupPath) };
    }

    public void Delete()
    {
        TryDelete(_profile.DataMaintenanceRequestPath);
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
}
