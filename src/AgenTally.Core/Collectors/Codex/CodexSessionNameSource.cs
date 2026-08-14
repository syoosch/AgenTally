using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors;
using AgenTally.Core.Collectors.Jsonl;
using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Core.Collectors.Codex;

internal sealed class CodexSessionNameSource : IUsageSessionNameSource, IDisposable
{
    internal const int MaximumNameLength = 120;
    private const int MaximumIdentityCharacters = 1024;
    private const int MaximumDatabaseTextCharacters = 4096;
    private const int MaximumIndexLineCharacters = 64 * 1024;
    private const int MaximumSessionNames = 100_000;
    private const long MaximumSessionIndexBytes = 64L * 1024 * 1024;

    private readonly string _databasePath;
    private readonly string _sessionIndexPath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<UsageSessionNameMetadata> _cachedNames = [];
    private SourceFingerprint? _cachedFingerprint;
    private bool _disposed;

    public CodexSessionNameSource(string codexHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codexHome);
        _databasePath = Path.Combine(
            CodexSourceIdentity.NormalizePath(codexHome),
            "state_5.sqlite");
        _sessionIndexPath = Path.Combine(
            CodexSourceIdentity.NormalizePath(codexHome),
            "session_index.jsonl");
    }

    public async Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        SourceFingerprint fingerprint = ReadFingerprint();
        if (_cachedFingerprint == fingerprint)
        {
            return _cachedNames;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            fingerprint = ReadFingerprint();
            if (_cachedFingerprint == fingerprint)
            {
                return _cachedNames;
            }

            IReadOnlyList<UsageSessionNameMetadata>? names =
                await TryReadNamesAsync(fingerprint, cancellationToken);
            if (names is not null)
            {
                _cachedNames = names;
                _cachedFingerprint = fingerprint;
            }

            return _cachedNames;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshGate.Dispose();
    }

    internal static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        int scalarCount = 0;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.Surrogate or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (pendingSpace)
            {
                if (scalarCount >= MaximumNameLength - 1)
                {
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
                scalarCount++;
            }

            if (scalarCount >= MaximumNameLength)
            {
                break;
            }

            builder.Append(rune.ToString());
            scalarCount++;
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private async Task<IReadOnlyList<UsageSessionNameMetadata>?> TryReadNamesAsync(
        SourceFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UsageSessionNameMetadata>? databaseNames =
            await TryReadDatabaseNamesAsync(fingerprint, cancellationToken);
        IReadOnlyList<UsageSessionNameMetadata>? indexedNames =
            await TryReadSessionIndexNamesAsync(fingerprint, cancellationToken);
        if (databaseNames is null || indexedNames is null)
        {
            return null;
        }

        var namesBySession = databaseNames.ToDictionary(
            value => value.SessionId,
            StringComparer.Ordinal);
        foreach (UsageSessionNameMetadata indexedName in indexedNames)
        {
            if (indexedName.SessionName is not null)
            {
                DateTimeOffset effectiveUpdatedAtUtc =
                    namesBySession.TryGetValue(
                        indexedName.SessionId,
                        out UsageSessionNameMetadata? databaseName) &&
                    databaseName.UpdatedAtUtc > indexedName.UpdatedAtUtc
                        ? databaseName.UpdatedAtUtc
                        : indexedName.UpdatedAtUtc;
                namesBySession[indexedName.SessionId] =
                    new UsageSessionNameMetadata(
                        indexedName.SessionId,
                        indexedName.SessionName,
                        effectiveUpdatedAtUtc);
            }
        }

        return namesBySession.Values
            .OrderBy(value => value.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<UsageSessionNameMetadata>?>
        TryReadDatabaseNamesAsync(
            SourceFingerprint fingerprint,
            CancellationToken cancellationToken)
    {
        if (!fingerprint.Database.Exists ||
            fingerprint.Database.IsReparsePoint ||
            fingerprint.WriteAheadLog.IsReparsePoint)
        {
            return [];
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            bool hasNameColumn =
                await HasNameColumnAsync(connection, cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    substr(CAST(id AS TEXT), 1, $identity_limit),
                    substr(CAST(title AS TEXT), 1, $text_limit),
                    {(hasNameColumn ? "substr(CAST(name AS TEXT), 1, $text_limit)" : "NULL")} AS name,
                    updated_at_ms
                FROM threads
                WHERE id IS NOT NULL
                  AND TRIM(id) <> ''
                ORDER BY id
                LIMIT $row_limit;
                """;
            command.Parameters.AddWithValue(
                "$identity_limit",
                MaximumIdentityCharacters + 1);
            command.Parameters.AddWithValue(
                "$text_limit",
                MaximumDatabaseTextCharacters + 1);
            command.Parameters.AddWithValue("$row_limit", MaximumSessionNames);

            var names = new List<UsageSessionNameMetadata>();
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long updatedAtUnixMs = reader.IsDBNull(3)
                    ? fingerprint.Database.LastWriteUnixMs
                    : reader.GetInt64(3);
                DateTimeOffset updatedAtUtc = FromUnixTimeMillisecondsOrFallback(
                    updatedAtUnixMs,
                    fingerprint.Database.LastWriteUnixMs);
                string? sourceName = NormalizeName(
                    reader.IsDBNull(2) ? null : reader.GetString(2));
                sourceName ??= NormalizeName(
                    reader.IsDBNull(1) ? null : reader.GetString(1));

                string? sessionId = NormalizeSessionId(reader.GetString(0));
                if (sessionId is null)
                {
                    continue;
                }

                names.Add(new UsageSessionNameMetadata(
                    sessionId,
                    sourceName,
                    updatedAtUtc));
            }

            return names;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is SqliteException
                or IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<UsageSessionNameMetadata>?>
        TryReadSessionIndexNamesAsync(
            SourceFingerprint fingerprint,
            CancellationToken cancellationToken)
    {
        if (!fingerprint.SessionIndex.Exists ||
            fingerprint.SessionIndex.IsReparsePoint)
        {
            return [];
        }

        try
        {
            var namesBySession = new Dictionary<
                string,
                UsageSessionNameMetadata>(StringComparer.Ordinal);
            await foreach (BoundedTextLine boundedLine in
                BoundedUtf8LineReader.ReadLinesAsync(
                    _sessionIndexPath,
                    MaximumIndexLineCharacters,
                    MaximumSessionIndexBytes,
                    cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (boundedLine.IsTooLong)
                {
                    continue;
                }

                string line = boundedLine.Text;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("id", out JsonElement idElement) ||
                        idElement.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(idElement.GetString()) ||
                        !root.TryGetProperty(
                            "thread_name",
                            out JsonElement nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? normalizedName = NormalizeName(
                        nameElement.GetString());
                    if (normalizedName is null)
                    {
                        continue;
                    }

                    DateTimeOffset updatedAtUtc = ReadIndexUpdatedAtUtc(
                        root,
                        fingerprint.SessionIndex.LastWriteUnixMs);
                    string? sessionId = NormalizeSessionId(
                        idElement.GetString());
                    if (sessionId is null ||
                        !namesBySession.ContainsKey(sessionId) &&
                        namesBySession.Count >= MaximumSessionNames)
                    {
                        continue;
                    }
                    namesBySession[sessionId] = new UsageSessionNameMetadata(
                        sessionId,
                        normalizedName,
                        updatedAtUtc);
                }
                catch (JsonException)
                {
                    // Ignore an incomplete or malformed metadata line. A later
                    // valid entry for the same id remains authoritative.
                }
            }

            return namesBySession.Values.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<bool> HasNameColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(threads);";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1) &&
                string.Equals(
                    reader.GetString(1),
                    "name",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset ReadIndexUpdatedAtUtc(
        JsonElement root,
        long fallbackUnixMs)
    {
        if (root.TryGetProperty(
                "updated_at",
                out JsonElement updatedAtElement) &&
            updatedAtElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                updatedAtElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset updatedAtUtc))
        {
            return updatedAtUtc;
        }

        return FromUnixTimeMillisecondsOrFallback(
            fallbackUnixMs,
            fallbackUnixMs);
    }

    private static string? NormalizeSessionId(string? value) =>
        value is { Length: > 0 and <= MaximumIdentityCharacters } &&
        !string.IsNullOrWhiteSpace(value) &&
        !value.Any(char.IsControl)
            ? value
            : null;

    private static DateTimeOffset FromUnixTimeMillisecondsOrFallback(
        long value,
        long fallback)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(fallback);
        }
    }

    private SourceFingerprint ReadFingerprint() =>
        new(
            ReadFileFingerprint(_databasePath),
            ReadFileFingerprint($"{_databasePath}-wal"),
            ReadFileFingerprint(_sessionIndexPath));

    private static FileFingerprint ReadFileFingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileFingerprint(false, false, 0, 0);
            }

            bool isReparsePoint =
                (info.Attributes & FileAttributes.ReparsePoint) != 0;
            return new FileFingerprint(
                true,
                isReparsePoint,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc)
                    .ToUnixTimeMilliseconds());
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return new FileFingerprint(false, false, 0, 0);
        }
    }

    private readonly record struct SourceFingerprint(
        FileFingerprint Database,
        FileFingerprint WriteAheadLog,
        FileFingerprint SessionIndex);

    private readonly record struct FileFingerprint(
        bool Exists,
        bool IsReparsePoint,
        long Length,
        long LastWriteUnixMs);
}
