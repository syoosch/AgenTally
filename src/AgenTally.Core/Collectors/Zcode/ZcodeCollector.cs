using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AgenTally.Core.Collectors.Codex;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Writing;
using Microsoft.Data.Sqlite;

namespace AgenTally.Core.Collectors.Zcode;

// The ZCode SQLite source selection and inclusive cache/reasoning normalization
// were informed by tokscale 4.8.1's MIT-licensed ZCode parser. AgenTally keeps a
// native bounded .NET collector and never embeds or invokes tokscale at runtime.
public sealed class ZcodeCollector : ISourceFileChangeCollector
{
    public const string CurrentParserVersion = "zcode-sqlite-v2";

    private const int MaxRowsPerBatch = 200;
    private const int MaxIdentityCharacters = 1024;
    private const int MaxModelCharacters = 512;
    private const int MaxPromptTextCharactersPerPart = 32_768;
    private const string BatchLimitCode = "collector.batch_limit_reached";

    private readonly string _zcodeHome;
    private readonly string _databasePath;
    private readonly TimeProvider _timeProvider;
    private readonly ZcodeSourceResolver _resolver;

    public ZcodeCollector(
        string zcodeHome,
        TimeProvider? timeProvider = null,
        ZcodeSourceResolver? resolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zcodeHome);
        _zcodeHome = ZcodeSourceIdentity.NormalizePath(zcodeHome);
        _databasePath = Path.Combine(
            _zcodeHome,
            "cli",
            "db",
            ZcodeSourceIdentity.DatabaseFileName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resolver = resolver ?? new ZcodeSourceResolver();
    }

    public string AgentId => "zcode";

    public string ParserVersion => CurrentParserVersion;

    public CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.PartiallyCompatible;

    public string WatchFilter => "db.sqlite*";

    public ValueTask<SourceProbeResult> ProbeAsync(
        CollectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _resolver.ProbeAsync(_zcodeHome, cancellationToken);
    }

    public async IAsyncEnumerable<CollectedBatch> CollectAsync(
        CollectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (request.Cursor is not null &&
            !string.Equals(
                request.Cursor.ParserVersion,
                CurrentParserVersion,
                StringComparison.Ordinal))
        {
            throw new AgentParserRebuildRequiredException(
                AgentId,
                request.Cursor.ParserVersion,
                CurrentParserVersion);
        }

        ZcodeCursor cursor;
        CollectorDiagnostic? cursorDiagnostic;
        if (request.Cursor is not null && !CursorBelongsToSource(request.Cursor))
        {
            cursor = ZcodeCursor.Start;
            cursorDiagnostic = new CollectorDiagnostic(
                "collector.cursor_source_mismatch",
                "The stored cursor did not belong to the ZCode database and was reset.",
                request.Entity.SourceEntityId);
        }
        else
        {
            cursor = ZcodeCursor.DeserializeOrStart(
                request.Cursor?.CursorJson,
                request.Cursor is not null,
                out cursorDiagnostic);
            if (cursorDiagnostic is not null)
            {
                cursorDiagnostic = cursorDiagnostic with
                {
                    SourceEntityId = request.Entity.SourceEntityId
                };
            }
        }

        string sourceFingerprint =
            ZcodeSourceIdentity.SourceFingerprint(_databasePath);
        string stampBefore = ZcodeCursor.ComputeSourceChangeStamp(_databasePath);
        // A row can be finalized after the preceding snapshot with the same
        // millisecond and an ID that sorts before the stored tie-breaker. When
        // the SQLite/WAL stamp changed, replay the last millisecond and let the
        // writer's stable model_usage.id identity discard proven duplicates.
        ZcodeCursor queryCursor = cursor.CompletedAtUnixMs >= 0 &&
            !string.Equals(
                cursor.SourceChangeStamp,
                stampBefore,
                StringComparison.Ordinal)
                ? cursor with { UsageId = string.Empty }
                : cursor;
        List<ZcodeUsageRow> rows;
        IReadOnlyDictionary<string, ZcodeSessionSnapshot> sessions;
        IReadOnlyDictionary<string, ZcodePromptSnapshot> prompts;

        try
        {
            await using (SqliteConnection connection = CreateReadOnlyConnection())
            {
                await connection.OpenAsync(cancellationToken);
                await using SqliteTransaction transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(
                        cancellationToken);
                rows = await ReadRowsAsync(
                    connection,
                    transaction,
                    queryCursor,
                    cancellationToken);
                sessions = await ReadSessionGraphAsync(
                    connection,
                    transaction,
                    rows.Select(static row => row.SessionId).Distinct(StringComparer.Ordinal),
                    cancellationToken);
                prompts = await ReadPromptSnapshotsAsync(
                    connection,
                    transaction,
                    rows,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            throw new InvalidDataException(
                "The ZCode usage database could not be read safely.",
                exception);
        }

        bool hasMore = rows.Count > MaxRowsPerBatch;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var events = new List<UsageEvent>(rows.Count);
        var sessionMetadata = new Dictionary<string, UsageSessionMetadata>(
            StringComparer.Ordinal);
        var turns = new Dictionary<string, UsageTurnMetadata>(StringComparer.Ordinal);
        var conflictingTurns = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset importedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();

        foreach (ZcodeUsageRow row in rows)
        {
            ZcodeMappedTokens mappedTokens = MapTokens(row);
            DateTimeOffset occurredAtUtc = ResolveOccurredAtUtc(row);
            UsageSessionMetadata currentSession = MapSession(
                row.SessionId,
                sessions,
                request,
                occurredAtUtc);
            AddSessionAndAncestors(
                row.SessionId,
                sessions,
                request,
                occurredAtUtc,
                sessionMetadata);

            string? turnIdHash = TryReadIdentity(row.TurnId, out string? turnId)
                ? ZcodeSourceIdentity.HashIdentity("zcode-turn", turnId!)
                : null;
            UsageEvent usageEvent = MapEvent(
                row,
                mappedTokens,
                currentSession,
                turnIdHash,
                request,
                sourceFingerprint,
                occurredAtUtc,
                importedAtUtc);
            events.Add(usageEvent);

            if (turnIdHash is not null &&
                TryMapTurn(
                    row,
                    turnIdHash,
                    request,
                    prompts,
                    out UsageTurnMetadata? turn))
            {
                string key = $"{row.SessionId}\0{turnIdHash}";
                if (!conflictingTurns.Contains(key) &&
                    turns.TryGetValue(key, out UsageTurnMetadata? existing) &&
                    existing != turn)
                {
                    turns.Remove(key);
                    conflictingTurns.Add(key);
                }
                else if (!conflictingTurns.Contains(key))
                {
                    turns[key] = turn!;
                }
            }
        }

        string stampAfter = ZcodeCursor.ComputeSourceChangeStamp(_databasePath);
        bool changedDuringCollection = !string.Equals(
            stampBefore,
            stampAfter,
            StringComparison.Ordinal);
        var diagnostics = new List<CollectorDiagnostic>();
        if (cursorDiagnostic is not null)
        {
            diagnostics.Add(cursorDiagnostic);
        }

        if (hasMore || changedDuringCollection)
        {
            diagnostics.Add(new CollectorDiagnostic(
                BatchLimitCode,
                hasMore
                    ? "The ZCode collection batch reached its bounded row limit."
                    : "The ZCode database changed during collection and will be checked again.",
                request.Entity.SourceEntityId));
        }

        string committedStamp = changedDuringCollection ? stampBefore : stampAfter;
        ZcodeCursor nextCursor = rows.Count == 0
            ? cursor with { SourceChangeStamp = committedStamp }
            : new ZcodeCursor(
                rows[^1].CompletedAtUnixMs,
                rows[^1].Id,
                committedStamp);
        yield return new CollectedBatch(
            request.Instance,
            request.Entity,
            events,
            nextCursor.Serialize(),
            sourceFingerprint,
            CurrentParserVersion,
            diagnostics)
        {
            Sessions = sessionMetadata.Values.ToArray(),
            Turns = turns.Values.ToArray()
        };
    }

    public IReadOnlyList<string> GetWatchRoots(SourceInstanceDescriptor instance)
    {
        ValidateInstance(instance);
        return [Path.Combine(instance.RootPath, "cli", "db")];
    }

    public string NormalizeSourcePath(string path) =>
        ZcodeSourceIdentity.NormalizePath(path);

    public string GetSourceEntityId(string normalizedChangePath) =>
        ZcodeSourceIdentity.EntityId(normalizedChangePath);

    public bool IsWithinMonitoredRoots(
        SourceInstanceDescriptor instance,
        string normalizedChangePath)
    {
        ValidateInstance(instance);
        if (!ZcodeSourceIdentity.IsDatabaseChangePath(normalizedChangePath))
        {
            return false;
        }

        string root = ZcodeSourceIdentity.NormalizePath(
            Path.Combine(instance.RootPath, "cli", "db"));
        string? parent = Path.GetDirectoryName(
            ZcodeSourceIdentity.NormalizePath(normalizedChangePath));
        return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsRelevantChangePath(string normalizedChangePath) =>
        ZcodeSourceIdentity.IsDatabaseChangePath(normalizedChangePath);

    public bool HasSourceChanged(
        SourceEntityDescriptor entity,
        StoredCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(cursor);
        ZcodeCursor parsed = ZcodeCursor.DeserializeOrStart(
            cursor.CursorJson,
            hasStoredCursor: true,
            out CollectorDiagnostic? diagnostic);
        return diagnostic is not null ||
               !string.Equals(
                   parsed.SourceChangeStamp,
                   ZcodeCursor.ComputeSourceChangeStamp(entity.SourcePath),
                   StringComparison.Ordinal);
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

    private static async Task<List<ZcodeUsageRow>> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ZcodeCursor cursor,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                mu.id,
                mu.session_id,
                mu.turn_id,
                mu.model_id,
                mu.status,
                mu.started_at,
                mu.completed_at,
                mu.duration_ms,
                mu.input_tokens,
                mu.output_tokens,
                mu.reasoning_tokens,
                mu.cache_read_input_tokens,
                mu.cache_creation_input_tokens,
                mu.provider_total_tokens,
                mu.computed_total_tokens,
                tu.started_at,
                tu.completed_at,
                tu.user_message_id
            FROM model_usage mu
            LEFT JOIN turn_usage tu
              ON tu.session_id = mu.session_id
             AND tu.turn_id = mu.turn_id
            WHERE mu.completed_at IS NOT NULL
              AND mu.completed_at > 0
              AND (
                    mu.completed_at > $completed_at
                    OR (
                        mu.completed_at = $completed_at
                        AND mu.id > $usage_id COLLATE BINARY
                    )
                  )
              AND COALESCE(mu.input_tokens, 0)
                + COALESCE(mu.output_tokens, 0)
                + COALESCE(mu.reasoning_tokens, 0)
                + COALESCE(mu.cache_read_input_tokens, 0)
                + COALESCE(mu.cache_creation_input_tokens, 0) > 0
            ORDER BY mu.completed_at, mu.id COLLATE BINARY
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$completed_at", cursor.CompletedAtUnixMs);
        command.Parameters.AddWithValue("$usage_id", (object?)cursor.UsageId ?? string.Empty);
        command.Parameters.AddWithValue("$limit", MaxRowsPerBatch + 1);

        var rows = new List<ZcodeUsageRow>(MaxRowsPerBatch + 1);
        try
        {
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new ZcodeUsageRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    ReadNullableString(reader, 2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    ReadNullableInt64(reader, 7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetInt64(12),
                    ReadNullableInt64(reader, 13),
                    reader.GetInt64(14),
                    ReadNullableInt64(reader, 15),
                    ReadNullableInt64(reader, 16),
                    ReadNullableString(reader, 17)));
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            throw new InvalidDataException(
                "The ZCode usage database schema is not supported safely.",
                exception);
        }

        return rows;
    }

    private static async Task<IReadOnlyDictionary<string, ZcodeSessionSnapshot>>
        ReadSessionGraphAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IEnumerable<string> sessionIds,
            CancellationToken cancellationToken)
    {
        var sessions = new Dictionary<string, ZcodeSessionSnapshot>(
            StringComparer.Ordinal);
        var pending = new Stack<string>(sessionIds.Where(static id =>
            !string.IsNullOrWhiteSpace(id)));
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, parent_id, directory, path, title, time_updated, task_type
            FROM session
            WHERE id = $id
            LIMIT 1;
            """;
        SqliteParameter idParameter = command.Parameters.Add("$id", SqliteType.Text);

        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sessionId = pending.Pop();
                if (sessions.ContainsKey(sessionId))
                {
                    continue;
                }

                idParameter.Value = sessionId;
                await using SqliteDataReader reader =
                    await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    sessions[sessionId] = ZcodeSessionSnapshot.Missing(sessionId);
                    continue;
                }

                var snapshot = new ZcodeSessionSnapshot(
                    reader.GetString(0),
                    ReadNullableString(reader, 1),
                    reader.GetString(2),
                    ReadNullableString(reader, 3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    Exists: true);
                sessions[sessionId] = snapshot;
                if (TryReadIdentity(snapshot.ParentId, out string? parentId) &&
                    !sessions.ContainsKey(parentId!))
                {
                    pending.Push(parentId!);
                }
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            throw new InvalidDataException(
                "The ZCode session schema is not supported safely.",
                exception);
        }

        return sessions;
    }

    private static async Task<IReadOnlyDictionary<string, ZcodePromptSnapshot>>
        ReadPromptSnapshotsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            IReadOnlyCollection<ZcodeUsageRow> rows,
            CancellationToken cancellationToken)
    {
        string[] messageIds = rows
            .Select(static row => row.TurnUserMessageId)
            .Where(static id => TryReadIdentity(id, out _))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (messageIds.Length == 0)
        {
            return new Dictionary<string, ZcodePromptSnapshot>(StringComparer.Ordinal);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[messageIds.Length];
        for (int index = 0; index < messageIds.Length; index++)
        {
            string parameterName = $"$message_id_{index}";
            parameters[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, messageIds[index]);
        }

        command.Parameters.AddWithValue(
            "$text_limit",
            MaxPromptTextCharactersPerPart);
        command.CommandText = $$"""
            SELECT
                m.id,
                m.session_id,
                json_extract(p.data, '$.type'),
                CASE
                    WHEN json_extract(p.data, '$.synthetic') = 1 THEN 1
                    ELSE 0
                END,
                substr(json_extract(p.data, '$.text'), 1, $text_limit),
                substr(json_extract(p.data, '$.mime'), 1, 256)
            FROM message m
            LEFT JOIN part p
              ON p.message_id = m.id
             AND p.session_id = m.session_id
             AND json_valid(p.data)
            WHERE m.id IN ({{string.Join(", ", parameters)}})
              AND json_valid(m.data)
              AND json_extract(m.data, '$.role') = 'user'
              AND COALESCE(json_extract(m.data, '$.synthetic'), 0) != 1
            ORDER BY
                m.id COLLATE BINARY,
                CASE WHEN p.sequence IS NULL THEN 1 ELSE 0 END,
                p.sequence,
                p.time_created,
                p.id COLLATE BINARY;
            """;

        var accumulators = new Dictionary<string, ZcodePromptAccumulator>(
            StringComparer.Ordinal);
        try
        {
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string messageId = reader.GetString(0);
                string sessionId = reader.GetString(1);
                if (!accumulators.TryGetValue(
                        messageId,
                        out ZcodePromptAccumulator? accumulator))
                {
                    accumulator = new ZcodePromptAccumulator(sessionId);
                    accumulators[messageId] = accumulator;
                }
                else if (!string.Equals(
                             accumulator.SessionId,
                             sessionId,
                             StringComparison.Ordinal))
                {
                    accumulators.Remove(messageId);
                    continue;
                }

                if (reader.IsDBNull(2) || reader.GetInt64(3) != 0)
                {
                    continue;
                }

                string partType = reader.GetString(2);
                string? text = ReadNullableString(reader, 4);
                string? mime = ReadNullableString(reader, 5);
                accumulator.AddPart(partType, text, mime);
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is not (5 or 6))
        {
            // Prompt metadata is additive. Older ZCode ledgers without the
            // message/part schema keep their exact usage data and explicitly
            // remain without previews instead of blocking collection.
            return new Dictionary<string, ZcodePromptSnapshot>(StringComparer.Ordinal);
        }

        return accumulators.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToSnapshot(),
            StringComparer.Ordinal);
    }

    private UsageEvent MapEvent(
        ZcodeUsageRow row,
        ZcodeMappedTokens mapped,
        UsageSessionMetadata session,
        string? turnIdHash,
        CollectionRequest request,
        string sourceFingerprint,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset importedAtUtc)
    {
        if (!TryReadIdentity(row.Id, out string? usageId) ||
            !TryReadIdentity(row.SessionId, out string? sessionId) ||
            !TryReadModel(row.ModelId, out string? rawModel))
        {
            throw new InvalidDataException(
                "A ZCode usage row contains an invalid stable identity.");
        }

        string dedupKey = ZcodeSourceIdentity.HashIdentity(
            "zcode-model-usage",
            usageId!);
        var tokens = new TokenUsage
        {
            InputReported = TokenMetric.Exact(row.InputTokens),
            UncachedInput = TokenMetric.Exact(mapped.UncachedInput),
            CacheRead = TokenMetric.Exact(row.CacheReadInputTokens),
            CacheWrite = TokenMetric.Exact(row.CacheCreationInputTokens),
            Output = TokenMetric.Exact(mapped.Output),
            Reasoning = TokenMetric.Exact(row.ReasoningTokens),
            Tool = TokenMetric.Unavailable,
            ReportedTotal = row.ProviderTotalTokens.HasValue
                ? TokenMetric.Exact(row.ProviderTotalTokens.Value)
                : TokenMetric.Unavailable,
            NormalizedTotal = TokenMetric.Exact(row.ComputedTotalTokens),
            CacheIncludedInInput = mapped.CacheInclusion,
            ReasoningIncludedInOutput = mapped.ReasoningInclusion
        };
        return new UsageEvent(
            AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            $"zcode-usage:{dedupKey[..32]}",
            dedupKey,
            SourceKind.Sqlite,
            occurredAtUtc,
            importedAtUtc,
            new ModelIdentity
            {
                RawModel = rawModel,
                NormalizedModel = rawModel!.ToLowerInvariant(),
                ProviderId = null,
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            tokens,
            string.Equals(row.Status, "completed", StringComparison.OrdinalIgnoreCase)
                ? CompletionState.Finalized
                : CompletionState.Completed,
            DataQuality.Exact,
            CurrentParserVersion,
            sourceFingerprint,
            row.CompletedAtUnixMs)
        {
            SessionId = sessionId,
            ParentSessionId = session.DirectParentSessionId,
            TurnIdHash = turnIdHash,
            ProjectId = session.ProjectId,
            ProjectPath = session.ProjectPath,
            ProjectRepositoryIdentityHash = session.ProjectRepositoryIdentityHash
        };
    }

    private static ZcodeMappedTokens MapTokens(ZcodeUsageRow row)
    {
        if (row.InputTokens < 0 ||
            row.OutputTokens < 0 ||
            row.ReasoningTokens < 0 ||
            row.CacheReadInputTokens < 0 ||
            row.CacheCreationInputTokens < 0 ||
            row.ComputedTotalTokens < 0 ||
            row.ProviderTotalTokens is < 0)
        {
            throw new InvalidDataException(
                "A ZCode usage row contains a negative Token value.");
        }

        try
        {
            long cacheOverlap = checked(
                row.CacheReadInputTokens + row.CacheCreationInputTokens);
            long inclusiveTotal = checked(row.InputTokens + row.OutputTokens);
            long exclusiveTotal = checked(
                inclusiveTotal + cacheOverlap + row.ReasoningTokens);

            if ((cacheOverlap > 0 || row.ReasoningTokens > 0) &&
                row.ComputedTotalTokens == inclusiveTotal &&
                row.ComputedTotalTokens != exclusiveTotal)
            {
                if (cacheOverlap > row.InputTokens ||
                    row.ReasoningTokens > row.OutputTokens)
                {
                    throw new InvalidDataException(
                        "A ZCode inclusive Token row contains impossible overlap.");
                }

                return new ZcodeMappedTokens(
                    row.InputTokens - cacheOverlap,
                    row.OutputTokens - row.ReasoningTokens,
                    MetricInclusion.Included,
                    MetricInclusion.Included);
            }

            if (row.ComputedTotalTokens == exclusiveTotal)
            {
                return new ZcodeMappedTokens(
                    row.InputTokens,
                    row.OutputTokens,
                    cacheOverlap == 0
                        ? MetricInclusion.Unknown
                        : MetricInclusion.Separate,
                    row.ReasoningTokens == 0
                        ? MetricInclusion.Unknown
                        : MetricInclusion.Separate);
            }

            throw new InvalidDataException(
                "A ZCode usage row does not prove inclusive or separate Token semantics.");
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "A ZCode usage row exceeds the supported Token range.",
                exception);
        }
    }

    private static DateTimeOffset ResolveOccurredAtUtc(ZcodeUsageRow row)
    {
        long timestamp = row.StartedAtUnixMs;
        if (timestamp <= 0 &&
            row.DurationMs is > 0 &&
            row.CompletedAtUnixMs > row.DurationMs.Value)
        {
            timestamp = row.CompletedAtUnixMs - row.DurationMs.Value;
        }
        if (timestamp <= 0)
        {
            timestamp = row.CompletedAtUnixMs;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "A ZCode usage row contains an invalid timestamp.",
                exception);
        }
    }

    private static UsageSessionMetadata MapSession(
        string sessionId,
        IReadOnlyDictionary<string, ZcodeSessionSnapshot> sessions,
        CollectionRequest request,
        DateTimeOffset fallbackTimeUtc)
    {
        if (!sessions.TryGetValue(sessionId, out ZcodeSessionSnapshot? source) ||
            !source.Exists)
        {
            return new UsageSessionMetadata(
                request.Instance.AgentId,
                request.Instance.SourceInstanceId,
                request.Entity.SourceEntityId,
                sessionId,
                SessionKind.Unknown,
                null,
                null,
                SessionRelationOrigin.None,
                SessionRelationState.None,
                ReplayState.Active,
                CompatibilityLevel.PartiallyCompatible,
                fallbackTimeUtc,
                CurrentParserVersion);
        }

        bool hasDeclaredParent = TryReadIdentity(source.ParentId, out string? parentId);
        bool confirmedParent = hasDeclaredParent &&
            sessions.TryGetValue(parentId!, out ZcodeSessionSnapshot? parent) &&
            parent.Exists &&
            HasAcyclicParentChain(sessionId, sessions);
        CodexProjectIdentity? project = CodexProjectIdentity.TryCreate(
            string.IsNullOrWhiteSpace(source.Directory)
                ? source.Path
                : source.Directory,
            out CodexProjectIdentity value)
                ? value
                : null;
        DateTimeOffset observedAtUtc = TryUnixMilliseconds(
            source.TimeUpdatedUnixMs,
            out DateTimeOffset updatedAtUtc)
                ? updatedAtUtc
                : fallbackTimeUtc;
        string? sessionName = NormalizeSourceName(source.Title);
        return new UsageSessionMetadata(
            request.Instance.AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            sessionId,
            confirmedParent ? SessionKind.Side : SessionKind.Primary,
            confirmedParent ? parentId : null,
            null,
            confirmedParent
                ? SessionRelationOrigin.SourceAgentParent
                : SessionRelationOrigin.None,
            confirmedParent
                ? SessionRelationState.Confirmed
                : SessionRelationState.None,
            ReplayState.Active,
            CompatibilityLevel.PartiallyCompatible,
            observedAtUtc,
            CurrentParserVersion)
        {
            ProjectId = project?.ProjectId,
            ProjectPath = project?.ProjectPath,
            ProjectRepositoryIdentityHash = project?.RepositoryIdentityHash,
            SessionRole = confirmedParent ? SessionRole.Unknown : SessionRole.Main,
            SessionName = sessionName,
            SessionNameUpdatedAtUtc = sessionName is null ? null : observedAtUtc
        };
    }

    private static void AddSessionAndAncestors(
        string sessionId,
        IReadOnlyDictionary<string, ZcodeSessionSnapshot> sessions,
        CollectionRequest request,
        DateTimeOffset fallbackTimeUtc,
        IDictionary<string, UsageSessionMetadata> metadata)
    {
        string? current = sessionId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (TryReadIdentity(current, out string? normalized) &&
               seen.Add(normalized!))
        {
            metadata[normalized!] = MapSession(
                normalized!,
                sessions,
                request,
                fallbackTimeUtc);
            current = sessions.TryGetValue(normalized!, out ZcodeSessionSnapshot? source)
                ? source.ParentId
                : null;
        }
    }

    private static bool TryMapTurn(
        ZcodeUsageRow row,
        string turnIdHash,
        CollectionRequest request,
        IReadOnlyDictionary<string, ZcodePromptSnapshot> prompts,
        out UsageTurnMetadata? turn)
    {
        turn = null;
        if (row.TurnStartedAtUnixMs is not > 0 ||
            !TryUnixMilliseconds(
                row.TurnStartedAtUnixMs.Value,
                out DateTimeOffset startedAtUtc))
        {
            return false;
        }

        DateTimeOffset? completedAtUtc = null;
        if (row.TurnCompletedAtUnixMs.HasValue)
        {
            if (!TryUnixMilliseconds(
                    row.TurnCompletedAtUnixMs.Value,
                    out DateTimeOffset completed) ||
                completed < startedAtUtc)
            {
                return false;
            }

            completedAtUtc = completed;
        }

        int userMessageCount = TryReadIdentity(
            row.TurnUserMessageId,
            out _)
                ? 1
                : 0;
        string? promptPreview = null;
        if (row.TurnUserMessageId is not null &&
            prompts.TryGetValue(
                row.TurnUserMessageId,
                out ZcodePromptSnapshot? prompt) &&
            string.Equals(
                prompt.SessionId,
                row.SessionId,
                StringComparison.Ordinal))
        {
            promptPreview = prompt.Preview;
        }

        turn = new UsageTurnMetadata(
            request.Instance.AgentId,
            request.Instance.SourceInstanceId,
            request.Entity.SourceEntityId,
            row.SessionId,
            turnIdHash,
            startedAtUtc,
            completedAtUtc,
            promptPreview,
            userMessageCount,
            CurrentParserVersion);
        return true;
    }

    private static bool HasAcyclicParentChain(
        string sessionId,
        IReadOnlyDictionary<string, ZcodeSessionSnapshot> sessions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        string? current = sessionId;
        while (current is not null &&
               sessions.TryGetValue(current, out ZcodeSessionSnapshot? source) &&
               TryReadIdentity(source.ParentId, out string? parentId))
        {
            if (!seen.Add(parentId!))
            {
                return false;
            }

            current = parentId;
        }

        return true;
    }

    private void ValidateRequest(CollectionRequest request)
    {
        string expectedInstanceId = ZcodeSourceIdentity.InstanceId(_zcodeHome);
        string expectedEntityId = ZcodeSourceIdentity.EntityId(_databasePath);
        if (!string.Equals(
                request.Instance.SourceInstanceId,
                expectedInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Instance.AgentId,
                AgentId,
                StringComparison.Ordinal) ||
            request.Instance.SourceKind != SourceKind.Sqlite ||
            !string.Equals(
                request.Entity.SourceInstanceId,
                expectedInstanceId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Entity.SourceEntityId,
                expectedEntityId,
                StringComparison.Ordinal) ||
            !string.Equals(
                ZcodeSourceIdentity.CanonicalDatabasePath(request.Entity.SourcePath),
                ZcodeSourceIdentity.CanonicalDatabasePath(_databasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The collection request does not belong to this ZCode source.",
                nameof(request));
        }
    }

    private void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.Equals(instance.AgentId, AgentId, StringComparison.Ordinal) ||
            !string.Equals(
                instance.SourceInstanceId,
                ZcodeSourceIdentity.InstanceId(_zcodeHome),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The source instance does not belong to this ZCode collector.",
                nameof(instance));
        }
    }

    private bool CursorBelongsToSource(StoredCursor cursor) =>
        string.Equals(
            cursor.SourceInstanceId,
            ZcodeSourceIdentity.InstanceId(_zcodeHome),
            StringComparison.Ordinal) &&
        string.Equals(
            cursor.SourceEntityId,
            ZcodeSourceIdentity.EntityId(_databasePath),
            StringComparison.Ordinal) &&
        string.Equals(
            ZcodeSourceIdentity.CanonicalDatabasePath(cursor.SourcePath),
            ZcodeSourceIdentity.CanonicalDatabasePath(_databasePath),
            StringComparison.OrdinalIgnoreCase);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static bool TryReadIdentity(string? value, out string? normalized)
    {
        normalized = null;
        if (value is not { Length: > 0 and <= MaxIdentityCharacters } ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    private static bool TryReadModel(string? value, out string? normalized)
    {
        normalized = null;
        if (value is not { Length: > 0 and <= MaxModelCharacters } ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length > 0;
    }

    private static bool TryUnixMilliseconds(
        long value,
        out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static string? NormalizeSourceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maximumScalars = 120;
        var normalized = new StringBuilder(maximumScalars);
        bool pendingSpace = false;
        int scalars = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) ||
                category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (scalars + 1 >= maximumScalars)
                {
                    break;
                }

                normalized.Append(' ');
                scalars++;
                pendingSpace = false;
            }

            if (scalars >= maximumScalars)
            {
                break;
            }

            normalized.Append(rune.ToString());
            scalars++;
        }

        return normalized.Length == 0 ? null : normalized.ToString();
    }

    private sealed record ZcodeUsageRow(
        string Id,
        string SessionId,
        string? TurnId,
        string ModelId,
        string Status,
        long StartedAtUnixMs,
        long CompletedAtUnixMs,
        long? DurationMs,
        long InputTokens,
        long OutputTokens,
        long ReasoningTokens,
        long CacheReadInputTokens,
        long CacheCreationInputTokens,
        long? ProviderTotalTokens,
        long ComputedTotalTokens,
        long? TurnStartedAtUnixMs,
        long? TurnCompletedAtUnixMs,
        string? TurnUserMessageId);

    private sealed record ZcodeSessionSnapshot(
        string Id,
        string? ParentId,
        string Directory,
        string? Path,
        string Title,
        long TimeUpdatedUnixMs,
        string TaskType,
        bool Exists)
    {
        public static ZcodeSessionSnapshot Missing(string id) => new(
            id,
            null,
            string.Empty,
            null,
            string.Empty,
            0,
            string.Empty,
            Exists: false);
    }

    private sealed class ZcodePromptAccumulator(string sessionId)
    {
        private readonly List<string> _textParts = [];
        private bool _hasImage;
        private bool _hasAudio;

        public string SessionId { get; } = sessionId;

        public void AddPart(string partType, string? text, string? mime)
        {
            switch (partType)
            {
                case "text" when !string.IsNullOrWhiteSpace(text):
                    _textParts.Add(text);
                    break;
                case "image":
                case "image_url":
                    _hasImage = true;
                    break;
                case "audio":
                case "input_audio":
                    _hasAudio = true;
                    break;
                case "file" when mime?.StartsWith(
                    "image/",
                    StringComparison.OrdinalIgnoreCase) is true:
                    _hasImage = true;
                    break;
                case "file" when mime?.StartsWith(
                    "audio/",
                    StringComparison.OrdinalIgnoreCase) is true:
                    _hasAudio = true;
                    break;
            }
        }

        public ZcodePromptSnapshot ToSnapshot()
        {
            var source = new StringBuilder(256);
            if (_hasImage)
            {
                source.Append("[图片]");
            }

            if (_hasAudio)
            {
                if (source.Length > 0)
                {
                    source.Append(' ');
                }

                source.Append("[音频]");
            }

            foreach (string text in _textParts)
            {
                if (source.Length > 0)
                {
                    source.Append(' ');
                }

                source.Append(text);
            }

            return new ZcodePromptSnapshot(
                SessionId,
                NormalizeSourceName(source.ToString()));
        }
    }

    private sealed record ZcodePromptSnapshot(
        string SessionId,
        string? Preview);

    private sealed record ZcodeMappedTokens(
        long UncachedInput,
        long Output,
        MetricInclusion CacheInclusion,
        MetricInclusion ReasoningInclusion);
}
