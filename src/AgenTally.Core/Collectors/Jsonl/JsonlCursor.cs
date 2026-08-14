using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Core.Collectors.Jsonl;

public sealed record JsonlCursor(
    long ByteOffset,
    string PendingBase64,
    long LineNumber,
    string SourceFingerprint)
{
    public const int MaxLogicalLineBytes = 8 * 1024 * 1024;
    public const int MaxPendingBytes = MaxLogicalLineBytes + 1;
    public const int MaxPendingBase64Characters = ((MaxPendingBytes + 2) / 3) * 4;
    public const int MaxSerializedCursorCharacters = MaxPendingBase64Characters + 1024;

    private const int MaxSourceFingerprintCharacters = 128;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static JsonlCursor Start { get; } = new(0, string.Empty, 0, string.Empty);

    [JsonIgnore]
    public byte[] PendingBytes => TryGetPendingBytes(out byte[] pendingBytes)
        ? pendingBytes
        : throw new FormatException("JSONL 读取游标无效。");

    public string Serialize()
    {
        if (!TryGetPendingBytes(out _))
        {
            throw new InvalidOperationException("JSONL 读取游标无效，无法序列化。");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        if (json.Length > MaxSerializedCursorCharacters)
        {
            throw new InvalidOperationException("JSONL 读取游标超过序列化上限。");
        }

        return json;
    }

    public static JsonlCursor DeserializeOrStart(
        string? cursorJson,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
        {
            diagnostic = null;
            return Start;
        }

        if (cursorJson.Length > MaxSerializedCursorCharacters)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }

        try
        {
            JsonlCursor? cursor = JsonSerializer.Deserialize<JsonlCursor>(cursorJson, JsonOptions);
            if (cursor is null || !cursor.TryGetPendingBytes(out _))
            {
                throw new JsonException("游标字段无效。");
            }

            diagnostic = null;
            return cursor;
        }
        catch (Exception exception)
            when (exception is JsonException or FormatException or ArgumentException or NotSupportedException)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }
    }

    internal bool TryGetPendingBytes(out byte[] pendingBytes)
    {
        pendingBytes = [];

        if (ByteOffset < 0 ||
            LineNumber < 0 ||
            PendingBase64 is null ||
            PendingBase64.Length > MaxPendingBase64Characters ||
            SourceFingerprint is null ||
            SourceFingerprint.Length > MaxSourceFingerprintCharacters)
        {
            return false;
        }

        if ((ByteOffset > 0 || LineNumber > 0) && string.IsNullOrWhiteSpace(SourceFingerprint))
        {
            return false;
        }

        try
        {
            pendingBytes = Convert.FromBase64String(PendingBase64);
            if (pendingBytes.Length > MaxPendingBytes ||
                pendingBytes.LongLength > ByteOffset ||
                Array.IndexOf(pendingBytes, (byte)'\n') >= 0)
            {
                pendingBytes = [];
                return false;
            }

            if (pendingBytes.Length == MaxPendingBytes &&
                pendingBytes[^1] != (byte)'\r')
            {
                pendingBytes = [];
                return false;
            }

            if (LineNumber == 0)
            {
                if (ByteOffset != 0 || pendingBytes.Length != 0)
                {
                    pendingBytes = [];
                    return false;
                }
            }
            else if (ByteOffset - pendingBytes.LongLength < LineNumber)
            {
                pendingBytes = [];
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "jsonl.invalid_cursor",
        "JSONL 读取游标无效，已从头重新读取。");
}
