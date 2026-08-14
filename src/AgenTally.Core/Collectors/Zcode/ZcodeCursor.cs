using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Core.Collectors.Zcode;

public sealed record ZcodeCursor(
    long CompletedAtUnixMs,
    string? UsageId,
    string SourceChangeStamp)
{
    private const int MaxUsageIdCharacters = 1024;
    private const int MaxSerializedCharacters = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ZcodeCursor Start { get; } = new(-1, null, string.Empty);

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException("ZCode collection cursor is invalid.");
        }

        string json = JsonSerializer.Serialize(this, JsonOptions);
        return json.Length <= MaxSerializedCharacters
            ? json
            : throw new InvalidOperationException(
                "ZCode collection cursor exceeds its size limit.");
    }

    public static ZcodeCursor DeserializeOrStart(
        string? cursorJson,
        bool hasStoredCursor,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(cursorJson))
        {
            diagnostic = hasStoredCursor ? InvalidCursorDiagnostic() : null;
            return Start;
        }

        if (cursorJson.Length > MaxSerializedCharacters)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }

        try
        {
            ZcodeCursor? cursor = JsonSerializer.Deserialize<ZcodeCursor>(
                cursorJson,
                JsonOptions);
            if (cursor is null || !cursor.IsValid())
            {
                throw new JsonException("Cursor fields are invalid.");
            }

            diagnostic = null;
            return cursor;
        }
        catch (Exception exception)
            when (exception is JsonException
                or NotSupportedException
                or ArgumentException
                or FormatException)
        {
            diagnostic = InvalidCursorDiagnostic();
            return Start;
        }
    }

    public static string ComputeSourceChangeStamp(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string canonicalPath = ZcodeSourceIdentity.CanonicalDatabasePath(databasePath);
        var source = new StringBuilder(256);
        // WAL contains durable committed changes. SHM only coordinates WAL locks
        // and a read-only connection may mutate it, so including SHM would let
        // this collector trigger itself indefinitely.
        foreach (string path in new[]
                 {
                     canonicalPath,
                     $"{canonicalPath}-wal"
                 })
        {
            var info = new FileInfo(path);
            info.Refresh();
            source.Append(Path.GetFileName(path).ToUpperInvariant());
            source.Append('\0');
            source.Append(info.Exists ? info.Length : -1);
            source.Append('\0');
            source.Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1);
            source.Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(source.ToString())))
            .ToLowerInvariant();
    }

    private bool IsValid()
    {
        if (CompletedAtUnixMs < -1 ||
            (CompletedAtUnixMs == -1) != (UsageId is null) ||
            UsageId is { Length: 0 or > MaxUsageIdCharacters } ||
            UsageId?.Any(char.IsControl) is true)
        {
            return false;
        }

        return this == Start || IsHash(SourceChangeStamp);
    }

    private static bool IsHash(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CollectorDiagnostic InvalidCursorDiagnostic() => new(
        "zcode.invalid_cursor",
        "The ZCode collection cursor was invalid and has been reset.");
}
