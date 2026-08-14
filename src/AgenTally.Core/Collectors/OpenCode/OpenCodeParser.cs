using System.Globalization;
using System.Text;
using System.Text.Json;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Runtime;
using Microsoft.Data.Sqlite;

namespace AgenTally.Core.Collectors.OpenCode;

internal static class OpenCodeParser
{
    internal const int MaxPayloadCharacters = 1024 * 1024;
    internal const int MaxLegacyFileBytes =
        (MaxPayloadCharacters * 4) + 4;
    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 512;
    private const int MaxWorkspacePathCharacters = 32767;

    internal static async Task<OpenCodeParsePage> ParseAsync(
        string path,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (OpenCodeSourceIdentity.IsDatabase(path))
        {
            return await ParseDatabaseAsync(path, offset, limit, cancellationToken);
        }

        if (offset > 0)
        {
            return new OpenCodeParsePage([], [], 0, false);
        }

        string json = await ReadLegacyJsonAsync(path, cancellationToken);
        var diagnostics = new List<CollectorDiagnostic>();
        OpenCodeParsedRecord? record = TryParseRecord(
            json,
            "legacy",
            Path.GetFileNameWithoutExtension(path),
            rowSessionId: null,
            rowRole: null,
            rowWorkspaceRoot: null,
            requireEmbeddedAssistantRole: true,
            diagnostics);
        return new OpenCodeParsePage(
            record is null ? [] : [record],
            diagnostics,
            1,
            false);
    }

    private static async Task<OpenCodeParsePage> ParseDatabaseAsync(
        string path,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 2
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        bool hasV1 = await HasTableAsync(connection, transaction, "message", cancellationToken);
        bool hasV2 = await HasTableAsync(
            connection,
            transaction,
            "session_message",
            cancellationToken);
        if (!hasV1 && !hasV2)
        {
            throw new InvalidDataException("The OpenCode database schema is not supported.");
        }

        bool hasSession = await HasTableAsync(
            connection,
            transaction,
            "session",
            cancellationToken);
        bool hasDirectory = hasSession && await HasColumnAsync(
            connection,
            transaction,
            "session",
            "directory",
            cancellationToken);
        var queries = new List<string>();
        if (hasV1)
        {
            queries.Add("SELECT '1' AS schema_kind, id AS raw_id, session_id AS raw_session_id, data AS raw_data, NULL AS raw_role FROM message");
        }
        if (hasV2)
        {
            queries.Add("SELECT '2' AS schema_kind, id AS raw_id, session_id AS raw_session_id, data AS raw_data, type AS raw_role FROM session_message");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT
                schema_kind,
                substr(CAST(raw_id AS TEXT), 1, $identity_limit),
                substr(CAST(raw_session_id AS TEXT), 1, $identity_limit),
                substr(CAST(raw_data AS TEXT), 1, $payload_limit),
                substr(CAST(raw_role AS TEXT), 1, $identity_limit)
            FROM ({string.Join(" UNION ALL ", queries)})
            ORDER BY
                schema_kind COLLATE BINARY,
                raw_id COLLATE BINARY,
                raw_session_id COLLATE BINARY
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue(
            "$identity_limit",
            MaxIdentityCharacters + 1);
        command.Parameters.AddWithValue(
            "$payload_limit",
            MaxPayloadCharacters + 1);
        var rows = new List<OpenCodeRawRow>(limit + 1);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new OpenCodeRawRow(
                    reader.GetString(0),
                    ReadRequiredIdentity(reader, 1),
                    ReadRequiredIdentity(reader, 2),
                    ReadRequiredPayload(reader, 3),
                    reader.IsDBNull(4) ? null : ReadRequiredIdentity(reader, 4)));
            }
        }

        bool hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var workspaceRoots = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (hasDirectory)
        {
            foreach (string sessionId in rows.Select(static row => row.SessionId)
                         .Distinct(StringComparer.Ordinal))
            {
                workspaceRoots[sessionId] = await ReadSessionDirectoryAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken);
            }
        }

        var diagnostics = new List<CollectorDiagnostic>();
        var records = new List<OpenCodeParsedRecord>();
        foreach (OpenCodeRawRow row in rows)
        {
            bool v1 = string.Equals(row.SchemaKind, "1", StringComparison.Ordinal);
            if (!v1 && !string.Equals(row.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            OpenCodeParsedRecord? record = TryParseRecord(
                row.Data,
                row.SchemaKind,
                row.RowId,
                row.SessionId,
                row.Role,
                workspaceRoots.GetValueOrDefault(row.SessionId),
                requireEmbeddedAssistantRole: v1,
                diagnostics);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        OpenCodeParsedRecord[] deduplicated = records
            .GroupBy(
                static record => $"{record.SessionId}\0{record.StableMessageId}",
                StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
        return new OpenCodeParsePage(
            deduplicated,
            diagnostics,
            rows.Count,
            hasMore);
    }

    private static OpenCodeParsedRecord? TryParseRecord(
        string json,
        string schemaKind,
        string rowId,
        string? rowSessionId,
        string? rowRole,
        string? rowWorkspaceRoot,
        bool requireEmbeddedAssistantRole,
        List<CollectorDiagnostic> diagnostics)
    {
        if (json.Length is 0 or > MaxPayloadCharacters)
        {
            diagnostics.Add(InvalidRecord());
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }

            string? embeddedRole = ReadString(root, MaxIdentityCharacters, "role");
            if (requireEmbeddedAssistantRole &&
                !string.Equals(embeddedRole, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (!requireEmbeddedAssistantRole &&
                !string.Equals(rowRole, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? normalizedRowSessionId = NormalizeIdentity(rowSessionId);
            if (rowSessionId is not null && normalizedRowSessionId is null)
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }
            string? embeddedSessionId = ReadString(
                root,
                MaxIdentityCharacters,
                "sessionID",
                "sessionId",
                "session_id");
            if (normalizedRowSessionId is not null && embeddedSessionId is not null &&
                !string.Equals(normalizedRowSessionId, embeddedSessionId, StringComparison.Ordinal))
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }
            string? sessionId = normalizedRowSessionId ?? embeddedSessionId;
            string? stableId = ReadString(root, MaxIdentityCharacters, "id") ??
                NormalizeIdentity(rowId);
            if (sessionId is null || stableId is null)
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }

            string? model = ReadString(root, MaxModelCharacters, "modelID", "modelId");
            string? provider = ReadString(root, MaxIdentityCharacters, "providerID", "providerId");
            if (root.TryGetProperty("model", out JsonElement modelObject) &&
                modelObject.ValueKind == JsonValueKind.Object)
            {
                model ??= ReadString(modelObject, MaxModelCharacters, "id");
                provider ??= ReadString(
                    modelObject,
                    MaxIdentityCharacters,
                    "providerID",
                    "providerId");
            }
            if (model is null ||
                !root.TryGetProperty("tokens", out JsonElement tokenValue) ||
                !TryParseTokens(tokenValue, out TokenUsage? tokens, out DataQuality quality))
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }

            DateTimeOffset? occurredAt = TryReadTimestamp(root);
            if (occurredAt is null)
            {
                diagnostics.Add(InvalidRecord());
                return null;
            }

            string? embeddedRoot = null;
            if (root.TryGetProperty("path", out JsonElement path) &&
                path.ValueKind == JsonValueKind.Object)
            {
                embeddedRoot = ReadString(path, 32767, "root");
            }
            string? workspaceRoot = ResolveWorkspaceRoot(rowWorkspaceRoot, embeddedRoot);
            return new OpenCodeParsedRecord(
                schemaKind,
                stableId,
                sessionId,
                model,
                provider,
                occurredAt.Value,
                workspaceRoot,
                tokens!,
                quality);
        }
        catch (JsonException)
        {
            diagnostics.Add(InvalidRecord());
            return null;
        }
    }

    private static bool TryParseTokens(
        JsonElement value,
        out TokenUsage? usage,
        out DataQuality quality)
    {
        usage = null;
        quality = DataQuality.Derived;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadOptionalCounter(value, "input", out long input, out bool hasInput) ||
            !TryReadOptionalCounter(value, "output", out long output, out bool hasOutput) ||
            !TryReadOptionalCounter(value, "reasoning", out long reasoning, out bool hasReasoning) ||
            !TryReadOptionalCounter(value, "total", out long total, out bool hasTotal))
        {
            return false;
        }
        long cacheRead = 0;
        long cacheWrite = 0;
        bool hasCacheRead = false;
        bool hasCacheWrite = false;
        bool hasCacheObject = value.TryGetProperty("cache", out JsonElement cache);
        if (hasCacheObject && cache.ValueKind != JsonValueKind.Object ||
            hasCacheObject &&
            (!TryReadOptionalCounter(cache, "read", out cacheRead, out hasCacheRead) ||
             !TryReadOptionalCounter(cache, "write", out cacheWrite, out hasCacheWrite)))
        {
            return false;
        }
        if (!(hasInput || hasOutput || hasReasoning || hasTotal ||
              hasCacheRead || hasCacheWrite))
        {
            return false;
        }

        try
        {
            long normalized = checked(input + output + reasoning + cacheRead + cacheWrite);
            if (hasTotal && total != normalized)
            {
                return false;
            }
            if (normalized == 0)
            {
                return false;
            }

            bool exact = hasInput && hasOutput && hasReasoning && hasCacheObject &&
                hasCacheRead && hasCacheWrite;
            static TokenMetric Metric(long number, bool sourceReported) => new(
                number,
                sourceReported ? MetricOrigin.Exact : MetricOrigin.Derived);
            usage = new TokenUsage
            {
                InputReported = Metric(input, hasInput),
                UncachedInput = Metric(input, hasInput),
                CacheRead = Metric(cacheRead, hasCacheRead),
                CacheWrite = Metric(cacheWrite, hasCacheWrite),
                Output = Metric(output, hasOutput),
                Reasoning = Metric(reasoning, hasReasoning),
                Tool = TokenMetric.Unavailable,
                ReportedTotal = hasTotal
                    ? new TokenMetric(total, MetricOrigin.Exact)
                    : TokenMetric.Unavailable,
                NormalizedTotal = new TokenMetric(
                    normalized,
                    hasTotal || exact ? MetricOrigin.Exact : MetricOrigin.Derived),
                CacheIncludedInInput = hasCacheObject
                    ? MetricInclusion.Separate
                    : MetricInclusion.Unknown,
                ReasoningIncludedInOutput = hasReasoning
                    ? MetricInclusion.Separate
                    : MetricInclusion.Unknown
            };
            quality = exact ? DataQuality.Exact : DataQuality.Derived;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("time", out JsonElement time) ||
            time.ValueKind != JsonValueKind.Object ||
            !time.TryGetProperty("created", out JsonElement created) ||
            created.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        long milliseconds;
        if (!created.TryGetInt64(out milliseconds))
        {
            if (!created.TryGetDouble(out double floating) || !double.IsFinite(floating) ||
                floating != Math.Truncate(floating) ||
                floating < long.MinValue || floating > long.MaxValue)
            {
                return null;
            }
            milliseconds = checked((long)floating);
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task<bool> HasTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{table}]);";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<string?> ReadSessionDirectoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT substr(CAST(directory AS TEXT), 1, $path_limit)
            FROM session
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue(
            "$path_limit",
            MaxWorkspacePathCharacters + 1);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text &&
            text.Length is > 0 and <= MaxWorkspacePathCharacters &&
            !text.Any(char.IsControl)
                ? text
                : null;
    }

    private static async Task<string> ReadLegacyJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] payload = await BoundedFileReader.ReadAllBytesAsync(
            path,
            MaxLegacyFileBytes,
            cancellationToken);
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            string json = await reader.ReadToEndAsync(cancellationToken);
            return json.Length <= MaxPayloadCharacters
                ? json
                : throw new InvalidDataException(
                    "The OpenCode message file is too large.");
        }
        finally
        {
            Array.Clear(payload);
        }
    }

    private static bool TryReadOptionalCounter(
        JsonElement value,
        string name,
        out long result,
        out bool present)
    {
        present = value.TryGetProperty(name, out JsonElement property);
        if (!present)
        {
            result = 0;
            return true;
        }
        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out result) && result >= 0)
        {
            return true;
        }
        result = 0;
        return false;
    }

    private static string? ReadString(
        JsonElement value,
        int maxCharacters,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (value.TryGetProperty(name, out JsonElement property) &&
                property.ValueKind == JsonValueKind.String &&
                property.GetString() is string text &&
                text.Length is > 0 && text.Length <= maxCharacters &&
                !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl))
            {
                return text.Trim();
            }
        }
        return null;
    }

    private static string? NormalizeIdentity(string? value) =>
        value is { Length: > 0 and <= MaxIdentityCharacters } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl)
            ? value.Trim()
            : null;

    private static string ReadRequiredIdentity(SqliteDataReader reader, int ordinal)
    {
        string value = ReadRequiredString(reader, ordinal);
        return NormalizeIdentity(value) ??
            throw new InvalidDataException("An OpenCode database identity is invalid.");
    }

    private static string ReadRequiredPayload(SqliteDataReader reader, int ordinal)
    {
        string value = ReadRequiredString(reader, ordinal);
        return value.Length <= MaxPayloadCharacters
            ? value
            : throw new InvalidDataException("An OpenCode database payload is too large.");
    }

    private static string ReadRequiredString(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidDataException("An OpenCode database row is missing a required value.");
        }
        return reader.GetString(ordinal);
    }

    private static string? ResolveWorkspaceRoot(string? rowRoot, string? embeddedRoot)
    {
        if (rowRoot is not null && embeddedRoot is not null)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(rowRoot),
                    Path.GetFullPath(embeddedRoot),
                    StringComparison.OrdinalIgnoreCase)
                        ? rowRoot
                        : null;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                or PathTooLongException)
            {
                return null;
            }
        }
        return rowRoot ?? embeddedRoot;
    }

    private static CollectorDiagnostic InvalidRecord() => new(
        "opencode.unsupported_token_record",
        "An OpenCode Token record could not prove safe identity or counter semantics and was skipped.");
}

internal sealed record OpenCodeParsePage(
    IReadOnlyList<OpenCodeParsedRecord> Records,
    IReadOnlyList<CollectorDiagnostic> Diagnostics,
    int RawRowsConsumed,
    bool HasMore);

internal sealed record OpenCodeParsedRecord(
    string SchemaKind,
    string StableMessageId,
    string SessionId,
    string Model,
    string? Provider,
    DateTimeOffset OccurredAtUtc,
    string? WorkspaceRoot,
    TokenUsage Tokens,
    DataQuality DataQuality);

internal sealed record OpenCodeRawRow(
    string SchemaKind,
    string RowId,
    string SessionId,
    string Data,
    string? Role);
