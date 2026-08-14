using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Core.Collectors.QwenCode;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using Microsoft.Data.Sqlite;

namespace AgenTally.Core.Collectors.Qoder;

// Qoder and Qoder CN use the same local assistant-message schema but separate
// application roots. The selection and correction behavior were cross-checked
// against TokenTracker current@5122d2e; AgenTally keeps a native read-only
// bounded collector and never invokes that project at runtime.
public sealed class QoderDesktopCollector : ISourceFileChangeCollector
{
    public const string CurrentParserVersion = "qoder-desktop-sqlite-v2";
    private const int MaxRowsPerBatch = 200;
    private const int MaxPromptSourceCharacters = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _root;
    private readonly string _databasePath;
    private readonly QoderEdition _edition;
    private readonly string _agentId;
    private readonly TimeProvider _timeProvider;
    private readonly QoderDesktopSourceResolver _resolver;

    public QoderDesktopCollector(
        string root,
        QoderEdition edition,
        TimeProvider? timeProvider = null,
        QoderDesktopSourceResolver? resolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = QoderSourceIdentity.NormalizePath(root);
        _databasePath = QoderSourceIdentity.DatabasePath(_root);
        _edition = edition;
        _agentId = QoderSourceIdentity.AgentId(edition);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new QoderDesktopSourceResolver();
    }

    public string AgentId => _agentId;

    public string ParserVersion => CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public string WatchFilter => "local.db*";

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_root, _edition, cancellationToken);
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (request.Cursor is not null &&
            !string.Equals(request.Cursor.ParserVersion, ParserVersion, StringComparison.Ordinal))
        {
            throw new AgentParserRebuildRequiredException(
                AgentId,
                request.Cursor.ParserVersion,
                ParserVersion);
        }

        CollectorDiagnostic? cursorDiagnostic = null;
        QoderDesktopCursor cursor;
        if (request.Cursor is not null &&
            (!string.Equals(request.Cursor.SourceInstanceId, request.Instance.SourceInstanceId,
                 StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceEntityId, request.Entity.SourceEntityId,
                 StringComparison.Ordinal) ||
             !string.Equals(request.Cursor.SourceFingerprint,
                 QoderSourceIdentity.SourceFingerprint($"{AgentId}-desktop", _databasePath),
                 StringComparison.Ordinal)))
        {
            cursor = QoderDesktopCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "collector.cursor_source_mismatch",
                "The stored cursor did not belong to this Qoder Desktop database and was reset.",
                request.Entity.SourceEntityId);
        }
        else
        {
            cursor = QoderDesktopCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                request.Cursor is not null,
                out cursorDiagnostic);
        }

        EnsureNoReparsePoints();
        string sourceFingerprint = QoderSourceIdentity.SourceFingerprint(
            $"{AgentId}-desktop",
            _databasePath);
        string stampBefore = QoderDesktopCursor.ComputeSourceChangeStamp(_databasePath);
        if (cursor.ScanSourceStamp.Length == 0)
        {
            if (request.Cursor is not null &&
                string.Equals(cursor.CompletedSourceStamp, stampBefore, StringComparison.Ordinal))
            {
                yield return new CollectedBatch(
                    request.Instance,
                    request.Entity,
                    [],
                    cursor.Serialize(),
                    sourceFingerprint,
                    ParserVersion,
                    cursorDiagnostic is null ? [] : [cursorDiagnostic]);
                yield break;
            }
            cursor = cursor.BeginScan(stampBefore);
        }

        List<QoderUsageRow> rows;
        try
        {
            await using SqliteConnection connection = CreateReadOnlyConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            rows = await ReadRowsAsync(connection, transaction, cursor.LastRowId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            throw new InvalidDataException(
                "The Qoder Desktop usage database could not be read safely.",
                exception);
        }

        bool hasMore = rows.Count > MaxRowsPerBatch;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        DateTimeOffset importedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var events = new List<UsageEvent>(rows.Count);
        var sessions = new Dictionary<string, UsageSessionMetadata>(StringComparer.Ordinal);
        var turns = new Dictionary<string, UsageTurnMetadata>(StringComparer.Ordinal);
        var diagnostics = new List<CollectorDiagnostic>();
        if (cursorDiagnostic is not null)
        {
            diagnostics.Add(cursorDiagnostic with { SourceEntityId = request.Entity.SourceEntityId });
        }
        foreach (QoderUsageRow row in rows)
        {
            QoderMappedRow mapped = MapRow(row, request, sourceFingerprint, cursor.ScanRevision,
                importedAtUtc);
            events.Add(mapped.Event);
            sessions[row.SessionId] = mapped.Session;
            if (mapped.Turn is not null)
            {
                turns[$"{row.SessionId}\0{mapped.Turn.TurnIdHash}"] = mapped.Turn;
            }
        }

        string stampAfter = QoderDesktopCursor.ComputeSourceChangeStamp(_databasePath);
        bool changedDuringCollection = !string.Equals(stampBefore, stampAfter, StringComparison.Ordinal);
        QoderDesktopCursor next;
        if (hasMore)
        {
            next = cursor with { LastRowId = rows[^1].RowId };
            diagnostics.Add(new CollectorDiagnostic(
                "collector.batch_limit_reached",
                "The Qoder Desktop collection reached its bounded row limit.",
                request.Entity.SourceEntityId));
        }
        else
        {
            next = new QoderDesktopCursor(
                cursor.ScanSourceStamp,
                "",
                0,
                0,
                cursor.NextRevision);
            if (changedDuringCollection)
            {
                diagnostics.Add(new CollectorDiagnostic(
                    "collector.batch_limit_reached",
                    "The Qoder Desktop database changed during collection and will be checked again.",
                    request.Entity.SourceEntityId));
            }
        }

        EnsureNoReparsePoints();
        yield return new CollectedBatch(
            request.Instance,
            request.Entity,
            events,
            next.Serialize(),
            sourceFingerprint,
            ParserVersion,
            diagnostics)
        {
            Sessions = sessions.Values.ToArray(),
            Turns = turns.Values.ToArray()
        };
    }

    public IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [Path.GetDirectoryName(_databasePath)!];
    }

    public string NormalizeSourcePath(string path) => QoderSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        QoderSourceIdentity.DesktopEntityId(normalizedChangePath, _edition);

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath)
    {
        ValidateInstance(instance);
        return QoderSourceIdentity.IsDatabaseChangePath(normalizedChangePath) &&
            string.Equals(
                Path.GetDirectoryName(QoderSourceIdentity.NormalizePath(normalizedChangePath)),
                Path.GetDirectoryName(_databasePath),
                StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRelevantChangePath(string normalizedChangePath) =>
        QoderSourceIdentity.IsDatabaseChangePath(normalizedChangePath);

    public bool HasSourceChanged(SourceEntityDescriptor entity, StoredCursor storedCursor)
    {
        QoderDesktopCursor cursor = QoderDesktopCursor.DeserializeOrStart(
            storedCursor.CursorJson,
            true,
            out CollectorDiagnostic? diagnostic);
        return diagnostic is not null || cursor.ScanSourceStamp.Length > 0 ||
               !string.Equals(cursor.CompletedSourceStamp,
                   QoderDesktopCursor.ComputeSourceChangeStamp(entity.SourcePath),
                   StringComparison.Ordinal);
    }

    private async Task<List<QoderUsageRow>> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long lastRowId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                m.rowid,
                CAST(m.id AS TEXT),
                CAST(m.session_id AS TEXT),
                CAST(m.request_id AS TEXT),
                m.token_info,
                m.model_info,
                m.gmt_create,
                (SELECT r.extra
                   FROM chat_record r
                  WHERE r.session_id = m.session_id
                    AND r.request_id = m.request_id
                  ORDER BY r.rowid
                  LIMIT 1),
                s.project_uri,
                s.session_title,
                s.gmt_modified,
                s.parent_session_id,
                s.preferred_model_info,
                CASE WHEN EXISTS (
                    SELECT 1 FROM chat_session p
                     WHERE p.session_id = s.parent_session_id
                ) THEN 1 ELSE 0 END,
                (SELECT COUNT(*)
                   FROM chat_message u
                  WHERE u.session_id = m.session_id
                    AND u.request_id = m.request_id
                    AND u.role = 'user'),
                (SELECT u.content
                   FROM chat_message u
                  WHERE u.session_id = m.session_id
                    AND u.request_id = m.request_id
                    AND u.role = 'user'
                  ORDER BY u.gmt_create, u.rowid
                  LIMIT 1),
                (SELECT u.gmt_create
                   FROM chat_message u
                  WHERE u.session_id = m.session_id
                    AND u.request_id = m.request_id
                    AND u.role = 'user'
                  ORDER BY u.gmt_create, u.rowid
                  LIMIT 1)
            FROM chat_message m
            LEFT JOIN chat_session s ON s.session_id = m.session_id
            WHERE m.rowid > $last_rowid
              AND m.role = 'assistant'
              AND m.token_info IS NOT NULL
              AND TRIM(m.token_info) <> ''
            ORDER BY m.rowid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$last_rowid", lastRowId);
        command.Parameters.AddWithValue("$limit", MaxRowsPerBatch + 1);

        var rows = new List<QoderUsageRow>(MaxRowsPerBatch + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QoderUsageRow(
                reader.GetInt64(0),
                RequiredString(reader, 1, "message id"),
                RequiredString(reader, 2, "session id"),
                NullableString(reader, 3),
                RequiredString(reader, 4, "token info"),
                NullableString(reader, 5),
                ReadInt64(reader, 6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableString(reader, 9),
                ReadNullableInt64(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                reader.GetInt64(13) == 1,
                ReadInt32(reader, 14),
                NullableString(reader, 15),
                ReadNullableInt64(reader, 16)));
        }
        return rows;
    }

    private QoderMappedRow MapRow(
        QoderUsageRow row,
        CollectionRequest request,
        string sourceFingerprint,
        long sourceRevision,
        DateTimeOffset importedAtUtc)
    {
        QoderTokens counters = ParseTokens(row.TokenInfo);
        DateTimeOffset occurredAtUtc = FromUnix(row.CreatedAt);
        string? projectPath = ResolveProjectPath(row.ProjectUri);
        CodexProjectIdentity? project = projectPath is not null &&
            CodexProjectIdentity.TryCreate(projectPath, out CodexProjectIdentity mappedProject)
                ? mappedProject
                : null;
        string? parent = row.ParentExists && ValidIdentity(row.ParentSessionId)
            ? row.ParentSessionId
            : null;
        string? title = QwenCodeJsonlParser.NormalizePreview(row.SessionTitle);
        DateTimeOffset observed = row.SessionModifiedAt.HasValue
            ? FromUnix(row.SessionModifiedAt.Value)
            : occurredAtUtc;
        var session = new UsageSessionMetadata(
            AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            row.SessionId,
            parent is null ? SessionKind.Primary : SessionKind.Side,
            parent,
            null,
            parent is null ? SessionRelationOrigin.None : SessionRelationOrigin.SourceAgentParent,
            parent is null ? SessionRelationState.None : SessionRelationState.Confirmed,
            ReplayState.Active,
            CompatibilityLevel.PartiallyCompatible,
            observed,
            ParserVersion)
        {
            ProjectId = project?.ProjectId,
            ProjectPath = project?.ProjectPath,
            ProjectRepositoryIdentityHash = project?.RepositoryIdentityHash,
            SessionRole = parent is null ? SessionRole.Main : SessionRole.Unknown,
            SessionName = title,
            SessionNameUpdatedAtUtc = title is null ? null : observed
        };

        ModelIdentity model = ResolveModel(row);
        long normalizedTotal = checked(counters.Prompt + counters.Completion);
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(counters.Prompt),
            UncachedInput = TokenMetric.Exact(counters.Prompt - counters.Cached),
            CacheRead = TokenMetric.Exact(counters.Cached),
            CacheWrite = TokenMetric.Unavailable,
            Output = TokenMetric.Exact(counters.Completion),
            Reasoning = TokenMetric.Unavailable,
            Tool = TokenMetric.Unavailable,
            ReportedTotal = TokenMetric.Unavailable,
            NormalizedTotal = TokenMetric.Exact(normalizedTotal),
            CacheIncludedInInput = MetricInclusion.Included,
            ReasoningIncludedInOutput = MetricInclusion.Unknown
        };
        string dedup = QoderSourceIdentity.HashIdentity(
            $"{AgentId}-desktop-message",
            $"{row.SessionId}\0{row.MessageId}");
        string? turnHash = ValidIdentity(row.RequestId)
            ? QoderSourceIdentity.HashIdentity(
                $"{AgentId}-desktop-turn",
                $"{row.SessionId}\0{row.RequestId}")
            : null;
        var usageEvent = new UsageEvent(
            AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            $"{AgentId}-desktop-call:{dedup[..32]}",
            dedup,
            SourceKind.Sqlite,
            occurredAtUtc,
            importedAtUtc,
            model,
            tokens,
            CompletionState.Finalized,
            DataQuality.Exact,
            ParserVersion,
            sourceFingerprint,
            sourceRevision)
        {
            SessionId = row.SessionId,
            ParentSessionId = parent,
            TurnIdHash = turnHash,
            ProjectId = project?.ProjectId,
            ProjectPath = project?.ProjectPath,
            ProjectRepositoryIdentityHash = project?.RepositoryIdentityHash
        };

        UsageTurnMetadata? turn = turnHash is null ? null : new UsageTurnMetadata(
            AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            row.SessionId,
            turnHash,
            row.UserCreatedAt.HasValue ? FromUnix(row.UserCreatedAt.Value) : occurredAtUtc,
            occurredAtUtc,
            NormalizePromptPreview(row.UserContent),
            row.UserMessageCount,
            ParserVersion);
        return new QoderMappedRow(usageEvent, session, turn);
    }

    private static QoderTokens ParseTokens(string json)
    {
        if (json.Length > 65536)
        {
            throw new InvalidDataException("Qoder token_info was too large.");
        }
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        long prompt = RequiredCounter(root, "prompt_tokens");
        long completion = RequiredCounter(root, "completion_tokens");
        long cached = OptionalCounter(root, "cached_tokens");
        if (cached > prompt)
        {
            throw new InvalidDataException("Qoder cached tokens exceeded prompt tokens.");
        }
        return new QoderTokens(prompt, completion, cached);
    }

    private ModelIdentity ResolveModel(QoderUsageRow row)
    {
        string? routeModelId =
            ReadJsonString(row.ModelInfo, "model_key", "modelKey") ??
            ReadNestedJsonString(row.RecordExtra, "modelConfig", "key") ??
            ReadNestedJsonString(row.RecordExtra, "model_config", "key") ??
            ReadJsonString(row.PreferredModelInfo, "model_key", "modelKey", "key");
        string rawModel = routeModelId ?? "qoder-agent";
        string? normalizedModel = ModelIdentityCanonicalizer.Canonicalize(
            rawModel,
            AgentId);
        bool exactQwenAlias = string.Equals(
            routeModelId,
            "qmodel_38max",
            StringComparison.OrdinalIgnoreCase);
        return new ModelIdentity
        {
            RawModel = rawModel,
            NormalizedModel = normalizedModel,
            RouteModelId = routeModelId,
            DisplayName = exactQwenAlias ? "Qwen3.8-Max" : null,
            ResolutionOrigin = routeModelId is null
                ? ModelResolutionOrigin.Unknown
                : exactQwenAlias
                    ? ModelResolutionOrigin.ExactAlias
                    : ModelResolutionOrigin.LogConfirmed
        };
    }

    internal static string? NormalizePromptPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaxPromptSourceCharacters ||
            IsOpaqueEncryptedPayload(value))
        {
            return null;
        }

        return QwenCodeJsonlParser.NormalizePreview(value);
    }

    private static bool IsOpaqueEncryptedPayload(string value)
    {
        if (value.Length < 24 || value.Length % 4 != 0)
        {
            return false;
        }

        byte[] decoded = new byte[(value.Length / 4) * 3];
        if (!Convert.TryFromBase64String(value, decoded, out int bytesWritten) ||
            bytesWritten < 16 ||
            bytesWritten % 16 != 0)
        {
            return false;
        }

        try
        {
            StrictUtf8.GetCharCount(decoded.AsSpan(0, bytesWritten));
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private static string? ReadJsonString(string? json, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            foreach (string name in names)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty(name, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    ValidIdentity(value.GetString(), 512))
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? ReadNestedJsonString(string? json, string container, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(container, out JsonElement nested) &&
                nested.ValueKind == JsonValueKind.Object &&
                nested.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                ValidIdentity(value.GetString(), 512))
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    private static string? ResolveProjectPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32768 || value.Any(char.IsControl))
        {
            return null;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }
        return Path.IsPathRooted(value) ? value : null;
    }

    private SqliteConnection CreateReadOnlyConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 2
        };
        return new SqliteConnection(builder.ToString());
    }

    private void ValidateRequest(CollectionRequest request)
    {
        ValidateInstance(request.Instance);
        string path = QoderSourceIdentity.CanonicalDatabasePath(request.Entity.SourcePath);
        if (!string.Equals(path, _databasePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.Entity.SourceInstanceId, request.Instance.SourceInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(request.Entity.SourceEntityId,
                QoderSourceIdentity.DesktopEntityId(path, _edition), StringComparison.Ordinal))
        {
            throw new ArgumentException("The Qoder Desktop collection request was invalid.", nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        if (instance is null ||
            !string.Equals(instance.SourceInstanceId,
                QoderSourceIdentity.DesktopInstanceId(_root, _edition), StringComparison.Ordinal) ||
            !string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            instance.SourceKind != SourceKind.Sqlite ||
            !string.Equals(QoderSourceIdentity.NormalizePath(instance.RootPath), _root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Qoder Desktop source instance was invalid.", nameof(instance));
        }
    }

    private void EnsureNoReparsePoints()
    {
        string? current = _databasePath;
        try
        {
            while (!string.IsNullOrWhiteSpace(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Qoder Desktop source path safety validation failed.");
                }
                if (string.Equals(current, _root, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Qoder Desktop source path safety validation failed.", exception);
        }
        throw new InvalidDataException("Qoder Desktop source path escaped its configured root.");
    }

    private static long RequiredCounter(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result < 0)
        {
            throw new InvalidDataException("A Qoder Token counter was invalid.");
        }
        return result;
    }

    private static long OptionalCounter(JsonElement root, string name) =>
        !root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? 0
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) && result >= 0
                ? result
                : throw new InvalidDataException("A Qoder Token counter was invalid.");

    private static DateTimeOffset FromUnix(long value)
    {
        try
        {
            long milliseconds = value > 10_000_000_000L ? value : checked(value * 1000);
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("A Qoder timestamp was invalid.", exception);
        }
    }

    private static string RequiredString(SqliteDataReader reader, int ordinal, string name)
    {
        string? value = NullableString(reader, ordinal);
        return ValidIdentity(value) ? value! :
            throw new InvalidDataException($"A Qoder {name} was invalid.");
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long ReadInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? throw new InvalidDataException("A Qoder integer was missing.")
            : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        long value = ReadInt64(reader, ordinal);
        return value is >= 0 and <= int.MaxValue
            ? (int)value
            : throw new InvalidDataException("A Qoder user-message count was invalid.");
    }

    private static bool ValidIdentity(string? value, int maximum = 1024) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private sealed record QoderTokens(long Prompt, long Completion, long Cached);

    private sealed record QoderMappedRow(
        UsageEvent Event,
        UsageSessionMetadata Session,
        UsageTurnMetadata? Turn);

    private sealed record QoderUsageRow(
        long RowId,
        string MessageId,
        string SessionId,
        string? RequestId,
        string TokenInfo,
        string? ModelInfo,
        long CreatedAt,
        string? RecordExtra,
        string? ProjectUri,
        string? SessionTitle,
        long? SessionModifiedAt,
        string? ParentSessionId,
        string? PreferredModelInfo,
        bool ParentExists,
        int UserMessageCount,
        string? UserContent,
        long? UserCreatedAt);
}
