using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenTally.Core.Collectors;

internal sealed record SnapshotSourceCursor(
    string CompletedSourceStamp,
    string ScanSourceStamp,
    string AfterKey,
    long ScanRevision,
    long NextRevision)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static SnapshotSourceCursor Start { get; } = new("", "", "", 0, 1);

    internal SnapshotSourceCursor BeginScan(string stamp) => new(
        CompletedSourceStamp,
        stamp,
        "",
        NextRevision,
        checked(NextRevision + 1));

    internal SnapshotSourceCursor ContinueAfter(string key) => this with
    {
        AfterKey = key
    };

    internal SnapshotSourceCursor Complete(string stamp) => new(
        stamp,
        "",
        "",
        0,
        NextRevision);

    internal SnapshotSourceCursor RestartAfterChange() => new(
        CompletedSourceStamp,
        "",
        "",
        0,
        NextRevision);

    internal string Serialize()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException("Snapshot collection cursor is invalid.");
        }

        string json = JsonSerializer.Serialize(this, Options);
        return json.Length <= 8192
            ? json
            : throw new InvalidOperationException("Snapshot collection cursor is too large.");
    }

    internal static SnapshotSourceCursor DeserializeOrStart(
        string? json,
        bool hasStoredCursor,
        string diagnosticCode,
        out CollectorDiagnostic? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            diagnostic = hasStoredCursor ? Invalid(diagnosticCode) : null;
            return Start;
        }

        try
        {
            SnapshotSourceCursor? cursor = JsonSerializer.Deserialize<SnapshotSourceCursor>(
                json,
                Options);
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
            diagnostic = Invalid(diagnosticCode);
            return Start;
        }
    }

    internal static string ComputeSourceChangeStamp(
        string sourcePath,
        bool includeSqliteSidecars)
    {
        string normalized = Path.GetFullPath(sourcePath);
        var source = new StringBuilder(256);
        IEnumerable<string> paths = includeSqliteSidecars
            ? new[] { normalized, $"{normalized}-wal" }
            : new[] { normalized };
        foreach (string path in paths)
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
            AfterKey.Length > 4096 || AfterKey.Any(char.IsControl) ||
            ScanRevision < 0 || NextRevision < 1)
        {
            return false;
        }

        bool scanning = ScanSourceStamp.Length > 0;
        return scanning
            ? ScanRevision > 0 && ScanRevision < NextRevision
            : AfterKey.Length == 0 && ScanRevision == 0;
    }

    private static bool ValidStamp(string value) =>
        value.Length == 0 ||
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static CollectorDiagnostic Invalid(string code) => new(
        code,
        "The snapshot collection cursor was invalid and has been reset.");
}
