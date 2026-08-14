using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Storage.Pricing;

public enum PriceCommandKind
{
    SetPriceOverride = 0,
    RestorePriceDefault = 1,
    RestoreAllPriceDefaults = 2
}

public enum PriceCommandResultCode
{
    Success = 0,
    Busy = 1,
    InvalidRequest = 2,
    UnsupportedProtocol = 3,
    Failed = 4
}

public sealed record PriceRatePayload(
    decimal InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? CacheWriteUsdPerMillion,
    decimal OutputUsdPerMillion,
    long? LongContextThresholdTokens,
    decimal LongContextInputMultiplier,
    decimal LongContextOutputMultiplier)
{
    public static PriceRatePayload FromRate(ModelPriceRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return new PriceRatePayload(
            rate.InputUsdPerMillion,
            rate.CachedInputUsdPerMillion,
            rate.CacheWriteUsdPerMillion,
            rate.OutputUsdPerMillion,
            rate.LongContextThresholdTokens,
            rate.LongContextInputMultiplier,
            rate.LongContextOutputMultiplier);
    }

    public ModelPriceRate ToRate(string normalizedModel) => new(
        normalizedModel,
        InputUsdPerMillion,
        CachedInputUsdPerMillion,
        CacheWriteUsdPerMillion,
        OutputUsdPerMillion,
        LongContextThresholdTokens,
        LongContextInputMultiplier,
        LongContextOutputMultiplier);
}

public sealed record PriceCommandRequest(
    int ProtocolVersion,
    string RequestId,
    PriceCommandKind Command,
    string? NormalizedModel,
    PriceRatePayload? Rate)
{
    public static PriceCommandRequest SetOverride(ModelPriceRate rate)
    {
        ArgumentNullException.ThrowIfNull(rate);
        return new PriceCommandRequest(
            PriceCommandProtocol.CurrentVersion,
            Guid.NewGuid().ToString("D"),
            PriceCommandKind.SetPriceOverride,
            rate.NormalizedModel,
            PriceRatePayload.FromRate(rate));
    }

    public static PriceCommandRequest RestoreDefault(string normalizedModel) =>
        new(
            PriceCommandProtocol.CurrentVersion,
            Guid.NewGuid().ToString("D"),
            PriceCommandKind.RestorePriceDefault,
            normalizedModel,
            null);

    public static PriceCommandRequest RestoreAllDefaults() => new(
        PriceCommandProtocol.CurrentVersion,
        Guid.NewGuid().ToString("D"),
        PriceCommandKind.RestoreAllPriceDefaults,
        null,
        null);
}

public sealed record PriceCommandResponse(
    int ProtocolVersion,
    string RequestId,
    PriceCommandResultCode Result,
    string MessageCode,
    int NewlyPricedRecords);

public static class PriceCommandMessageCodes
{
    public const string PriceUpdated = "pricing_updated";
    public const string PriceDefaultRestored = "pricing_default_restored";
    public const string AllPriceDefaultsRestored =
        "pricing_all_defaults_restored";
    public const string Busy = "pricing_busy";
    public const string InvalidRequest = "pricing_invalid_request";
    public const string UnsupportedProtocol = "pricing_protocol_unsupported";
    public const string OperationTimedOut = "pricing_operation_timed_out";
    public const string Failed = "pricing_failed";
}

public static class PriceCommandProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 16 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(value);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            SerializerOptions);
        if (payload.Length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Pricing command frame is outside the allowed size.");
        }

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException("Pricing command frame is outside the allowed size.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions) ??
            throw new InvalidDataException("Pricing command frame did not contain a value.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Pricing command connection closed before the frame completed.");
            }

            offset += read;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
