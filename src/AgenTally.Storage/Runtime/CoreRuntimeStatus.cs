using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Storage.Runtime;

public enum CoreRuntimePhase
{
    Starting,
    UpdatingStatistics,
    Running,
    Stopping,
    Stopped,
    NeedsParserRebuild,
    NeedsParserRescan,
    SourceUnavailable,
    DatabaseUnavailable,
    Failed
}

public enum CoreRuntimeErrorCode
{
    None,
    AlreadyRunning,
    SourceUnavailable,
    ParserRebuildRequired,
    ParserRescanRequired,
    DatabaseUnavailable,
    SchemaIncompatible,
    ProtocolIncompatible,
    UnexpectedFailure
}

public sealed record CoreRuntimeStatus(
    int ProtocolVersion,
    AgenTallyChannel Channel,
    string ProfileId,
    string ApplicationVersion,
    int ProcessId,
    long ProcessStartUtcTicks,
    CoreRuntimePhase Phase,
    CoreRuntimeErrorCode ErrorCode,
    string MessageCode,
    DateTimeOffset ChangedAtUtc,
    int? ExitCode)
{
    public const int CurrentProtocolVersion = 1;
}

public sealed class CoreRuntimeStatusStore
{
    private const int MaximumStatusBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();
    private readonly AgenTallyRuntimeProfile _profile;

    public CoreRuntimeStatusStore(AgenTallyRuntimeProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (_profile.Channel == AgenTallyChannel.Development &&
            !_profile.IsDevelopmentOwnedPath(_profile.StatusPath))
        {
            throw new InvalidOperationException(
                "Development status must stay inside artifacts/development.");
        }
    }

    public async Task<CoreRuntimeStatus?> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_profile.StatusPath))
        {
            return null;
        }

        byte[] payload = await BoundedFileReader.ReadAllBytesAsync(
            _profile.StatusPath,
            MaximumStatusBytes,
            cancellationToken);
        try
        {
            CoreRuntimeStatus? status = JsonSerializer.Deserialize<CoreRuntimeStatus>(
                payload,
                SerializerOptions);
            if (status is null)
            {
                throw new InvalidDataException("Core runtime status is empty.");
            }

            Validate(status);
            return status;
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    public async Task WriteAsync(
        CoreRuntimeStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        Validate(status);
        Directory.CreateDirectory(_profile.RuntimeRoot);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
        RejectPrivateValues(json);

        string temporaryPath = _profile.StatusPath + ".tmp";
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            await ReplaceStatusFileAsync(
                temporaryPath,
                _profile.StatusPath,
                cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A concurrent reader never depends on the temporary file.
            }
        }
    }

    private static async Task ReplaceStatusFileAsync(
        string temporaryPath,
        string statusPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 40;
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(25);
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, statusPath, overwrite: true);
                return;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException &&
                      attempt < maximumAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private void Validate(CoreRuntimeStatus status)
    {
        if (status.ProtocolVersion != CoreRuntimeStatus.CurrentProtocolVersion ||
            status.Channel != _profile.Channel ||
            !string.Equals(
                status.ProfileId,
                _profile.ProfileId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(status.ApplicationVersion) ||
            status.ApplicationVersion.Length > 128 ||
            status.ProcessId <= 0 ||
            status.ProcessStartUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(status.MessageCode) ||
            status.MessageCode.Length > 96 ||
            status.MessageCode.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) ||
            status.ChangedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Core runtime status does not match the active profile contract.");
        }
    }

    private void RejectPrivateValues(byte[] json)
    {
        string text = System.Text.Encoding.UTF8.GetString(json);
        foreach (string forbidden in new[]
                 {
                     _profile.RepositoryRoot,
                     _profile.ApplicationRoot,
                     _profile.DataRoot,
                     _profile.RuntimeRoot,
                     _profile.DatabasePath,
                     _profile.CodexHome,
                     _profile.CoreExecutablePath
                 }.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Core runtime status contains a private path.");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
