using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Core.Collectors.Qoder;

public sealed record QoderDesktopCursor(
    string CompletedSourceStamp,
    string ScanSourceStamp,
    long LastRowId,
    long ScanRevision,
    long NextRevision)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static QoderDesktopCursor Start { get; } = new("", "", 0, 0, 1);

    public QoderDesktopCursor BeginScan(string stamp) => new(
        CompletedSourceStamp,
        stamp,
        0,
        NextRevision,
        checked(NextRevision + 1));

    public string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException("Qoder Desktop collection cursor is invalid.");
        }
        string json = JsonSerializer.Serialize(this, Options);
        return json.Length <= 4096
            ? json
            : throw new InvalidOperationException("Qoder Desktop collection cursor is too large.");
    }

    public static QoderDesktopCursor DeserializeOrStart(
        string? json,
        bool hasStoredCursor,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostic = hasStoredCursor ? Invalid() : null;
            return Start;
        }
        try
        {
            QoderDesktopCursor? cursor = JsonSerializer.Deserialize<QoderDesktopCursor>(json, Options);
            if (cursor is null || !cursor.IsValid())
            {
                throw new JsonException("Invalid cursor.");
            }
            diagnostic = null;
            return cursor;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException
            or ArgumentException or FormatException or OverflowException)
        {
            diagnostic = Invalid();
            return Start;
        }
    }

    public static string ComputeSourceChangeStamp(string databasePath)
    {
        string canonical = QoderSourceIdentity.CanonicalDatabasePath(databasePath);
        var source = new StringBuilder(256);
        foreach (string path in new[] { canonical, $"{canonical}-wal" })
        {
            var info = new FileInfo(path);
            info.Refresh();
            source.Append(Path.GetFileName(path).ToUpperInvariant()).Append('\0')
                .Append(info.Exists ? info.Length : -1).Append('\0')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1).Append('\0');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())))
            .ToLowerInvariant();
    }

    private bool IsValid()
    {
        if (!ValidStamp(CompletedSourceStamp) || !ValidStamp(ScanSourceStamp) ||
            LastRowId < 0 || ScanRevision < 0 || NextRevision < 1)
        {
            return false;
        }
        bool scanning = ScanSourceStamp.Length > 0;
        return scanning
            ? ScanRevision > 0 && ScanRevision < NextRevision
            : LastRowId == 0 && ScanRevision == 0;
    }

    private static bool ValidStamp(string value) =>
        value.Length == 0 || (value.Length == 64 && value.All(static c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f'));

    private static CollectorDiagnostic Invalid() => new(
        "qoder.invalid_cursor",
        "The Qoder Desktop collection cursor was invalid and has been reset.");
}
