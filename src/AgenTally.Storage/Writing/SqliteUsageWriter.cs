using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Writing;

public sealed class SqliteUsageWriter : IUsageWriter
{
    private const string UpsertInstance = """
        INSERT INTO source_instances (
            source_instance_id,
            agent_id,
            source_kind,
            display_name,
            root_path,
            last_checked_unix_ms
        ) VALUES (
            $source_instance_id,
            $agent_id,
            $source_kind,
            $display_name,
            $root_path,
            $checked_at
        )
        ON CONFLICT(source_instance_id) DO UPDATE SET
            agent_id = excluded.agent_id,
            source_kind = excluded.source_kind,
            display_name = excluded.display_name,
            root_path = excluded.root_path,
            last_checked_unix_ms = excluded.last_checked_unix_ms;
        """;

    private const string UpsertSuccessfulCursor = """
        INSERT INTO source_cursors (
            source_instance_id,
            source_entity_id,
            source_path,
            cursor_json,
            source_fingerprint,
            parser_version,
            event_revision_high_watermark,
            last_success_unix_ms,
            last_error,
            last_error_unix_ms
        ) VALUES (
            $source_instance_id,
            $source_entity_id,
            $source_path,
            $cursor_json,
            $source_fingerprint,
            $parser_version,
            $event_revision_high_watermark,
            $checked_at,
            NULL,
            NULL
        )
        ON CONFLICT(source_instance_id, source_entity_id) DO UPDATE SET
            source_path = excluded.source_path,
            cursor_json = excluded.cursor_json,
            source_fingerprint = excluded.source_fingerprint,
            parser_version = excluded.parser_version,
            event_revision_high_watermark =
                excluded.event_revision_high_watermark,
            last_success_unix_ms = excluded.last_success_unix_ms,
            last_error = NULL,
            last_error_unix_ms = NULL;
        """;

    private const string UpsertFailedCursor = """
        INSERT INTO source_cursors (
            source_instance_id,
            source_entity_id,
            source_path,
            cursor_json,
            source_fingerprint,
            parser_version,
            last_success_unix_ms,
            last_error,
            last_error_unix_ms
        ) VALUES (
            $source_instance_id,
            $source_entity_id,
            $source_path,
            NULL,
            NULL,
            NULL,
            NULL,
            $error,
            $failed_at
        )
        ON CONFLICT(source_instance_id, source_entity_id) DO UPDATE SET
            last_error = excluded.last_error,
            last_error_unix_ms = excluded.last_error_unix_ms;
        """;

    private readonly SqliteConnectionFactory _connections;
    private readonly OfflinePriceCatalog _priceCatalog;

    public SqliteUsageWriter(
        SqliteConnectionFactory connections,
        OfflinePriceCatalog? priceCatalog = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _priceCatalog = priceCatalog ?? OfflinePriceCatalog.Default;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await DatabaseSchema.InitializeAsync(connection, cancellationToken);
        await SqlitePriceBinder.ApplyCatalogUpgradeAsync(
            connection,
            _priceCatalog,
            cancellationToken);
    }

    public async Task<StoredCursor?> GetCursorAsync(
        string sourceInstanceId,
        string sourceEntityId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceEntityId);

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                source_instance_id,
                source_entity_id,
                source_path,
                cursor_json,
                source_fingerprint,
                parser_version,
                event_revision_high_watermark,
                last_success_unix_ms,
                last_error,
                last_error_unix_ms
            FROM source_cursors
            WHERE source_instance_id = $source_instance_id
              AND source_entity_id = $source_entity_id;
            """;
        command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        command.Parameters.AddWithValue("$source_entity_id", sourceEntityId);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(3))
        {
            return null;
        }

        return new StoredCursor(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadNullableTimestamp(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            ReadNullableTimestamp(reader, 9))
        {
            EventRevisionHighWatermark = reader.IsDBNull(6)
                ? null
                : reader.GetInt64(6)
        };
    }

    public async Task<SourceInstanceParserState> GetSourceInstanceParserStateAsync(
        SourceInstanceDescriptor instance,
        string requiredParserVersion,
        CancellationToken cancellationToken)
    {
        ValidateInstance(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredParserVersion);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH event_parser_state (parser_version) AS (
                SELECT usage_events.parser_version
                FROM usage_events
                WHERE usage_events.agent_id = $agent_id
                  AND usage_events.source_instance_id = $source_instance_id
            ),
            cursor_parser_state (parser_version) AS (
                SELECT source_cursors.parser_version
                FROM source_cursors
                INNER JOIN source_instances
                    ON source_instances.source_instance_id =
                       source_cursors.source_instance_id
                WHERE source_cursors.source_instance_id = $source_instance_id
                  AND source_instances.agent_id = $agent_id
                  AND source_cursors.cursor_json IS NOT NULL
            ),
            instance_agent_state (requires_reclassification) AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM source_instances
                    WHERE source_instance_id = $source_instance_id
                      AND agent_id <> $agent_id
                )
            )
            SELECT
                EXISTS (SELECT 1 FROM event_parser_state)
                    OR EXISTS (SELECT 1 FROM cursor_parser_state)
                    OR (SELECT requires_reclassification
                        FROM instance_agent_state),
                EXISTS (
                    SELECT 1
                    FROM cursor_parser_state
                    WHERE parser_version IS NULL
                       OR parser_version <> $required_parser_version
                )
                OR (
                    COALESCE(
                        (
                            SELECT accepted_parser_version
                            FROM source_instances
                            WHERE source_instance_id = $source_instance_id
                              AND agent_id = $agent_id
                        ),
                        ''
                    ) <> $required_parser_version
                    AND EXISTS (
                        SELECT 1
                        FROM event_parser_state
                        WHERE parser_version IS NULL
                           OR parser_version <> $required_parser_version
                    )
                )
                OR (SELECT requires_reclassification
                    FROM instance_agent_state);
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        command.Parameters.AddWithValue(
            "$required_parser_version",
            requiredParserVersion);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Source parser state query returned no result.");
        }

        return new SourceInstanceParserState(
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0);
    }

    public async Task<IReadOnlyList<StoredUsageSourceEntity>>
        GetSourceEntitiesWithUsageEventsAsync(
            string agentId,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                source_instance_id,
                source_entity_id
            FROM usage_events
            WHERE agent_id = $agent_id
            ORDER BY source_instance_id, source_entity_id;
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredUsageSourceEntity>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredUsageSourceEntity(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return result;
    }

    public async Task<WriteResult> CommitAsync(
        UsageEventBatch batch,
        CancellationToken cancellationToken)
    {
        ValidateBatch(batch);

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await UpsertInstanceAsync(
            connection,
            transaction,
            batch.Instance,
            batch.CheckedAtUtc,
            cancellationToken);

        int appliedCount = 0;
        await UpsertSessionsAsync(
            connection,
            transaction,
            batch.Sessions,
            cancellationToken);
        await UpsertTurnsAsync(
            connection,
            transaction,
            batch.Turns,
            cancellationToken);
        await UpsertDispatchesAsync(
            connection,
            transaction,
            batch.Dispatches,
            cancellationToken);
        await UpdateObservedCompatibilityAsync(
            connection,
            transaction,
            batch,
            cancellationToken);
        await using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = UsageEventSql.Upsert;

            foreach (UsageEvent value in batch.Events)
            {
                UsageEventSql.Bind(upsert, value, batch.Intent);
                appliedCount += await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await ReconcileTouchedProjectPathsAsync(
            connection,
            transaction,
            batch.Events,
            batch.Sessions,
            cancellationToken);

        await UpsertEventToolsAsync(
            connection,
            transaction,
            batch.EventTools,
            cancellationToken);
        await RebuildTurnAttributionsAsync(
            connection,
            transaction,
            batch.Instance.AgentId,
            batch.Instance.SourceInstanceId,
            cancellationToken);
        await SqlitePriceBinder.BindBatchAsync(
            connection,
            transaction,
            batch.Events,
            _priceCatalog,
            cancellationToken);
        await UpsertSuccessAsync(connection, transaction, batch, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new WriteResult(appliedCount, batch.Events.Count - appliedCount);
    }

    public async Task SynchronizeSessionNamesAsync(
        SourceInstanceDescriptor instance,
        IReadOnlyList<UsageSessionNameMetadata> sessionNames,
        CancellationToken cancellationToken)
    {
        ValidateInstance(instance);
        ArgumentNullException.ThrowIfNull(sessionNames);

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE usage_sessions
            SET session_name = $session_name,
                session_name_updated_unix_ms = $updated_at_unix_ms
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id
              AND session_id = $session_id
              AND (
                  session_name_updated_unix_ms IS NULL
                  OR session_name_updated_unix_ms <= $updated_at_unix_ms
              );
            """;

        foreach (UsageSessionNameMetadata value in sessionNames)
        {
            ArgumentNullException.ThrowIfNull(value);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$agent_id", instance.AgentId);
            command.Parameters.AddWithValue(
                "$source_instance_id",
                instance.SourceInstanceId);
            command.Parameters.AddWithValue("$session_id", value.SessionId);
            command.Parameters.AddWithValue(
                "$session_name",
                (object?)value.SessionName ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$updated_at_unix_ms",
                value.UpdatedAtUtc.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetSourceInstanceAsync(
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        ValidateInstance(instance);

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (SqliteCommand deleteEvents = connection.CreateCommand())
        {
            deleteEvents.Transaction = transaction;
            deleteEvents.CommandText = """
                DELETE FROM usage_events
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;

                DELETE FROM usage_turn_attributions
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;

                DELETE FROM usage_turn_dispatches
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;

                DELETE FROM usage_turns
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;

                DELETE FROM usage_sessions
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;
                """;
            deleteEvents.Parameters.AddWithValue("$agent_id", instance.AgentId);
            deleteEvents.Parameters.AddWithValue(
                "$source_instance_id",
                instance.SourceInstanceId);
            await deleteEvents.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (SqliteCommand deleteCursors = connection.CreateCommand())
        {
            deleteCursors.Transaction = transaction;
            deleteCursors.CommandText = """
                DELETE FROM source_cursors
                WHERE source_instance_id = $source_instance_id
                  AND EXISTS (
                      SELECT 1
                      FROM source_instances
                      WHERE source_instances.source_instance_id =
                            source_cursors.source_instance_id
                        AND source_instances.agent_id = $agent_id
                  );
                """;
            deleteCursors.Parameters.AddWithValue("$agent_id", instance.AgentId);
            deleteCursors.Parameters.AddWithValue(
                "$source_instance_id",
                instance.SourceInstanceId);
            await deleteCursors.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDatabasePath);
        if (instances.Count == 0)
        {
            return;
        }

        SourceInstanceDescriptor[] distinctInstances = instances
            .DistinctBy(static value => value.SourceInstanceId, StringComparer.Ordinal)
            .ToArray();
        if (distinctInstances.Length != instances.Count)
        {
            throw new ArgumentException(
                "Source instances must be unique.",
                nameof(instances));
        }

        foreach (SourceInstanceDescriptor instance in distinctInstances)
        {
            ValidateInstance(instance);
        }

        string fullStagingPath = Path.GetFullPath(stagingDatabasePath);
        if (!File.Exists(fullStagingPath))
        {
            throw new FileNotFoundException(
                "The staged rebuild database does not exist.",
                fullStagingPath);
        }

        if (string.Equals(
                fullStagingPath,
                _connections.DatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The staged rebuild database must differ from the primary database.",
                nameof(stagingDatabasePath));
        }

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await AttachStagingAsync(connection, fullStagingPath, cancellationToken);
        await ValidateStagingSchemaAsync(connection, cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (SourceInstanceDescriptor instance in distinctInstances)
        {
            await ReconcileUniqueRepositoryProjectsAsync(
                connection,
                transaction,
                instance,
                includeExistingSource: false,
                cancellationToken: cancellationToken);
            await DeleteSourceInstanceDerivedDataAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await UpsertInstanceAsync(
                connection,
                transaction,
                instance,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await CopyStagedCursorsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await CopyStagedEventsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await CopyStagedSessionsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await CopyStagedTurnMetadataAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken)
    {
        ValidateAcceptedParserVersion(acceptedParserVersion);
        return MergeSourceInstancesFromStagingAsync(
            instances.Select(instance => new SourceInstanceMaintenanceState(
                instance,
                acceptedParserVersion)).ToArray(),
            stagingDatabasePath,
            cancellationToken);
    }

    public async Task MergeSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken)
    {
        SourceInstanceMaintenanceState[] distinctInstances =
            ValidateMaintenanceRequest(instances, stagingDatabasePath);
        if (distinctInstances.Length == 0)
        {
            return;
        }

        string fullStagingPath = Path.GetFullPath(stagingDatabasePath);
        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await AttachStagingAsync(connection, fullStagingPath, cancellationToken);
        await ValidateStagingSchemaAsync(connection, cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (SourceInstanceMaintenanceState maintenance in distinctInstances)
        {
            SourceInstanceDescriptor instance = maintenance.Instance;
            bool requiresReclassification =
                await RequiresSourceInstanceReclassificationAsync(
                    connection,
                    transaction,
                    instance,
                    cancellationToken);
            if (requiresReclassification)
            {
                await DeleteSourceInstanceDerivedDataAsync(
                    connection,
                    transaction,
                    instance,
                    cancellationToken);
            }

            await UpsertInstanceAsync(
                connection,
                transaction,
                instance,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await ReconcileUniqueRepositoryProjectsAsync(
                connection,
                transaction,
                instance,
                includeExistingSource: !requiresReclassification,
                cancellationToken: cancellationToken);
            await PreserveBoundStagedPricesAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await MergeStagedEventsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await DeleteMissingStagedCursorsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            IReadOnlyList<UsageSessionMetadata> sessions =
                await ReadStagedSessionsAsync(
                    connection,
                    transaction,
                    instance,
                    cancellationToken);
            await UpsertSessionsAsync(
                connection,
                transaction,
                sessions,
                cancellationToken);
            await MergeStagedTurnMetadataAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await RebuildTurnAttributionsAsync(
                connection,
                transaction,
                instance.AgentId,
                instance.SourceInstanceId,
                cancellationToken);
            await MergeStagedCursorsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await UpdateCompatibilityAsync(
                connection,
                transaction,
                instance,
                maintenance.CompatibilityLevel,
                maintenance.CompatibilityCode,
                requiresRescan: false,
                maintenance.AcceptedParserVersion,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool>
        RequiresSourceInstanceReclassificationAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SourceInstanceDescriptor instance,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM source_instances
                WHERE source_instance_id = $source_instance_id
                  AND agent_id <> $agent_id
            );
            """;
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) != 0;
    }

    public async Task ClearSourceInstancesFromStagingAsync(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath,
        string acceptedParserVersion,
        CancellationToken cancellationToken)
    {
        ValidateAcceptedParserVersion(acceptedParserVersion);
        SourceInstanceDescriptor[] distinctInstances =
            ValidateStagingRequest(instances, stagingDatabasePath);
        if (distinctInstances.Length == 0)
        {
            return;
        }

        string fullStagingPath = Path.GetFullPath(stagingDatabasePath);
        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await AttachStagingAsync(connection, fullStagingPath, cancellationToken);
        await ValidateStagingSchemaAsync(connection, cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (SourceInstanceDescriptor instance in distinctInstances)
        {
            await DeleteSourceInstanceDerivedDataAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await UpsertInstanceAsync(
                connection,
                transaction,
                instance,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await CopyStagedCursorsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await UpdateCompatibilityAsync(
                connection,
                transaction,
                instance,
                CompatibilityLevel.FullyCompatible,
                null,
                requiresRescan: false,
                acceptedParserVersion,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAllStatisticsFromStagingAsync(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath,
        CancellationToken cancellationToken)
    {
        SourceInstanceMaintenanceState[] distinctInstances =
            ValidateMaintenanceRequest(instances, stagingDatabasePath);
        string fullStagingPath = Path.GetFullPath(stagingDatabasePath);
        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await AttachStagingAsync(connection, fullStagingPath, cancellationToken);
        await ValidateStagingSchemaAsync(connection, cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await DeleteAllStatisticsAsync(connection, transaction, cancellationToken);
        foreach (SourceInstanceMaintenanceState maintenance in distinctInstances)
        {
            SourceInstanceDescriptor instance = maintenance.Instance;
            await UpsertInstanceAsync(
                connection,
                transaction,
                instance,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await CopyStagedCursorsAsync(
                connection,
                transaction,
                instance,
                cancellationToken);
            await UpdateCompatibilityAsync(
                connection,
                transaction,
                instance,
                maintenance.CompatibilityLevel,
                maintenance.CompatibilityCode,
                requiresRescan: false,
                maintenance.AcceptedParserVersion,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetSourceCompatibilityAsync(
        SourceInstanceDescriptor instance,
        CompatibilityLevel compatibilityLevel,
        string? compatibilityCode,
        bool requiresRescan,
        CancellationToken cancellationToken)
    {
        ValidateInstance(instance);
        if (!Enum.IsDefined(compatibilityLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(compatibilityLevel));
        }

        if (compatibilityCode is { Length: > 96 } ||
            compatibilityCode?.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) is true)
        {
            throw new ArgumentException(
                "Compatibility code is invalid.",
                nameof(compatibilityCode));
        }

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertInstanceAsync(
            connection,
            transaction,
            instance,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await UpdateCompatibilityAsync(
            connection,
            transaction,
            instance,
            compatibilityLevel,
            compatibilityCode,
            requiresRescan,
            acceptedParserVersion: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(
        SourceInstanceDescriptor instance,
        SourceEntityDescriptor entity,
        string error,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateInstance(instance);
        ValidateEntity(entity, instance.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ValidateUtc(failedAtUtc, nameof(failedAtUtc));

        await using SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await UpsertInstanceAsync(
            connection,
            transaction,
            instance,
            failedAtUtc,
            cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertFailedCursor;
        BindEntity(command, entity);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue(
            "$failed_at",
            failedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AttachStagingAsync(
        SqliteConnection connection,
        string stagingDatabasePath,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $staging_path AS rebuild_stage;";
        command.Parameters.AddWithValue("$staging_path", stagingDatabasePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateStagingSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT user_version FROM rebuild_stage.pragma_user_version),
                (
                    SELECT COUNT(*)
                    FROM rebuild_stage.sqlite_schema
                    WHERE type = 'table'
                      AND name IN (
                          'source_instances',
                          'source_cursors',
                          'usage_events',
                          'usage_sessions',
                          'usage_turns',
                          'usage_event_tools',
                          'usage_turn_dispatches',
                          'usage_turn_attributions',
                          'pricing_overrides',
                          'pricing_catalog_state'
                      )
                );
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt32(0) != DatabaseSchema.CurrentVersion ||
            reader.GetInt32(1) != 10)
        {
            throw new InvalidDataException(
                "The staged rebuild database schema is incompatible.");
        }
    }

    private SourceInstanceDescriptor[] ValidateStagingRequest(
        IReadOnlyList<SourceInstanceDescriptor> instances,
        string stagingDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDatabasePath);
        SourceInstanceDescriptor[] distinctInstances = instances
            .DistinctBy(static value => value.SourceInstanceId, StringComparer.Ordinal)
            .ToArray();
        if (distinctInstances.Length != instances.Count)
        {
            throw new ArgumentException(
                "Source instances must be unique.",
                nameof(instances));
        }

        foreach (SourceInstanceDescriptor instance in distinctInstances)
        {
            ValidateInstance(instance);
        }

        string fullStagingPath = Path.GetFullPath(stagingDatabasePath);
        if (!File.Exists(fullStagingPath))
        {
            throw new FileNotFoundException(
                "The staged maintenance database does not exist.",
                fullStagingPath);
        }

        if (string.Equals(
                fullStagingPath,
                _connections.DatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The staged maintenance database must differ from the primary database.",
                nameof(stagingDatabasePath));
        }

        return distinctInstances;
    }

    private SourceInstanceMaintenanceState[] ValidateMaintenanceRequest(
        IReadOnlyList<SourceInstanceMaintenanceState> instances,
        string stagingDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(instances);
        foreach (SourceInstanceMaintenanceState maintenance in instances)
        {
            ArgumentNullException.ThrowIfNull(maintenance);
        }

        SourceInstanceMaintenanceState[] distinctInstances = instances
            .DistinctBy(
                static value => value.Instance.SourceInstanceId,
                StringComparer.Ordinal)
            .ToArray();
        if (distinctInstances.Length != instances.Count)
        {
            throw new ArgumentException(
                "Source instances must be unique.",
                nameof(instances));
        }

        foreach (SourceInstanceMaintenanceState maintenance in distinctInstances)
        {
            ValidateInstance(maintenance.Instance);
            ValidateAcceptedParserVersion(maintenance.AcceptedParserVersion);
            if (!Enum.IsDefined(maintenance.CompatibilityLevel))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(instances),
                    "Source compatibility level is invalid.");
            }

            ValidateCompatibilityCode(maintenance.CompatibilityCode);
        }

        _ = ValidateStagingRequest(
            distinctInstances.Select(static value => value.Instance).ToArray(),
            stagingDatabasePath);
        return distinctInstances;
    }

    private static async Task ReconcileUniqueRepositoryProjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        bool includeExistingSource,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS unique_project_repositories (
                project_path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                repository_hash TEXT NOT NULL
            );

            DELETE FROM unique_project_repositories;

            INSERT INTO unique_project_repositories (
                project_path,
                repository_hash
            )
            SELECT
                candidate.project_path,
                MIN(candidate.repository_hash)
            FROM (
                SELECT
                    project_path,
                    project_repository_hash AS repository_hash
                FROM rebuild_stage.usage_events

                UNION ALL

                SELECT
                    project_path,
                    project_repository_hash AS repository_hash
                FROM rebuild_stage.usage_sessions

                UNION ALL

                SELECT
                    project_path,
                    project_repository_hash AS repository_hash
                FROM usage_events
                WHERE $include_existing_source = 1
                   OR agent_id <> $agent_id
                   OR source_instance_id <> $source_instance_id

                UNION ALL

                SELECT
                    project_path,
                    project_repository_hash AS repository_hash
                FROM usage_sessions
                WHERE $include_existing_source = 1
                   OR agent_id <> $agent_id
                   OR source_instance_id <> $source_instance_id
            ) AS candidate
            WHERE candidate.project_path IS NOT NULL
              AND LENGTH(candidate.repository_hash) = 64
              AND candidate.repository_hash NOT GLOB '*[^0-9a-f]*'
            GROUP BY candidate.project_path COLLATE NOCASE
            HAVING COUNT(DISTINCT candidate.repository_hash) = 1;

            UPDATE rebuild_stage.usage_events AS staged
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE staged.agent_id = $agent_id
              AND staged.source_instance_id = $source_instance_id
              AND mapping.project_path = staged.project_path
              AND (
                  staged.project_repository_hash IS NULL
                  OR (
                      staged.project_repository_hash = mapping.repository_hash
                      AND COALESCE(staged.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );

            UPDATE rebuild_stage.usage_sessions AS staged
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE staged.agent_id = $agent_id
              AND staged.source_instance_id = $source_instance_id
              AND mapping.project_path = staged.project_path
              AND (
                  staged.project_repository_hash IS NULL
                  OR (
                      staged.project_repository_hash = mapping.repository_hash
                      AND COALESCE(staged.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );

            UPDATE usage_events AS existing
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE mapping.project_path = existing.project_path
              AND (
                  existing.project_repository_hash IS NULL
                  OR (
                      existing.project_repository_hash = mapping.repository_hash
                      AND COALESCE(existing.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );

            UPDATE usage_sessions AS existing
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE mapping.project_path = existing.project_path
              AND (
                  existing.project_repository_hash IS NULL
                  OR (
                      existing.project_repository_hash = mapping.repository_hash
                      AND COALESCE(existing.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        command.Parameters.AddWithValue(
            "$include_existing_source",
            includeExistingSource ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReconcileTouchedProjectPathsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageEvent> events,
        IReadOnlyList<UsageSessionMetadata> sessions,
        CancellationToken cancellationToken)
    {
        var projectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UsageEvent value in events)
        {
            if (!string.IsNullOrWhiteSpace(value.ProjectPath))
            {
                projectPaths.Add(value.ProjectPath);
            }
        }

        foreach (UsageSessionMetadata value in sessions)
        {
            if (!string.IsNullOrWhiteSpace(value.ProjectPath))
            {
                projectPaths.Add(value.ProjectPath);
            }
        }

        if (projectPaths.Count == 0)
        {
            return;
        }

        await using (SqliteCommand prepare = connection.CreateCommand())
        {
            prepare.Transaction = transaction;
            prepare.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS touched_project_paths (
                    project_path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY
                );

                DELETE FROM touched_project_paths;
                """;
            await prepare.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO touched_project_paths (project_path)
                VALUES ($project_path);
                """;
            SqliteParameter pathParameter =
                insert.Parameters.Add("$project_path", SqliteType.Text);
            foreach (string projectPath in projectPaths)
            {
                pathParameter.Value = projectPath;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using SqliteCommand reconcile = connection.CreateCommand();
        reconcile.Transaction = transaction;
        reconcile.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS unique_project_repositories (
                project_path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                repository_hash TEXT NOT NULL
            );

            DELETE FROM unique_project_repositories;

            INSERT INTO unique_project_repositories (
                project_path,
                repository_hash
            )
            SELECT
                candidate.project_path,
                MIN(candidate.repository_hash)
            FROM (
                SELECT
                    existing.project_path,
                    existing.project_repository_hash AS repository_hash
                FROM usage_events AS existing
                INNER JOIN touched_project_paths AS touched
                    ON touched.project_path = existing.project_path

                UNION ALL

                SELECT
                    existing.project_path,
                    existing.project_repository_hash AS repository_hash
                FROM usage_sessions AS existing
                INNER JOIN touched_project_paths AS touched
                    ON touched.project_path = existing.project_path
            ) AS candidate
            WHERE LENGTH(candidate.repository_hash) = 64
              AND candidate.repository_hash NOT GLOB '*[^0-9a-f]*'
            GROUP BY candidate.project_path COLLATE NOCASE
            HAVING COUNT(DISTINCT candidate.repository_hash) = 1;

            UPDATE usage_events AS existing
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE mapping.project_path = existing.project_path
              AND (
                  existing.project_repository_hash IS NULL
                  OR (
                      existing.project_repository_hash = mapping.repository_hash
                      AND COALESCE(existing.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );

            UPDATE usage_sessions AS existing
            SET project_repository_hash = mapping.repository_hash,
                project_id = SUBSTR(mapping.repository_hash, 1, 24)
            FROM unique_project_repositories AS mapping
            WHERE mapping.project_path = existing.project_path
              AND (
                  existing.project_repository_hash IS NULL
                  OR (
                      existing.project_repository_hash = mapping.repository_hash
                      AND COALESCE(existing.project_id, '') <>
                          SUBSTR(mapping.repository_hash, 1, 24)
                  )
              );
            """;
        await reconcile.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PreserveBoundStagedPricesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE rebuild_stage.usage_events AS staged
            SET (
                price_catalog_version,
                price_rule_id,
                input_rate_usd_per_million,
                cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million,
                output_rate_usd_per_million,
                price_context_multiplier,
                output_price_context_multiplier,
                estimated_cost_usd,
                pricing_status,
                pricing_missing_categories
            ) = (
                SELECT
                    matched.price_catalog_version,
                    matched.price_rule_id,
                    matched.input_rate_usd_per_million,
                    matched.cached_input_rate_usd_per_million,
                    matched.cache_write_rate_usd_per_million,
                    matched.output_rate_usd_per_million,
                    matched.price_context_multiplier,
                    matched.output_price_context_multiplier,
                    matched.estimated_cost_usd,
                    matched.pricing_status,
                    matched.pricing_missing_categories
                FROM (
                    SELECT
                        existing.price_catalog_version,
                        existing.price_rule_id,
                        existing.input_rate_usd_per_million,
                        existing.cached_input_rate_usd_per_million,
                        existing.cache_write_rate_usd_per_million,
                        existing.output_rate_usd_per_million,
                        existing.price_context_multiplier,
                        existing.output_price_context_multiplier,
                        existing.estimated_cost_usd,
                        existing.pricing_status,
                        existing.pricing_missing_categories,
                        existing.dedup_key AS match_dedup_key,
                        0 AS match_priority
                    FROM usage_events AS existing
                    WHERE existing.agent_id = staged.agent_id
                      AND existing.source_instance_id = staged.source_instance_id
                      AND existing.dedup_key = staged.dedup_key
                      AND existing.pricing_status <> 0

                    UNION ALL

                    SELECT
                        existing.price_catalog_version,
                        existing.price_rule_id,
                        existing.input_rate_usd_per_million,
                        existing.cached_input_rate_usd_per_million,
                        existing.cache_write_rate_usd_per_million,
                        existing.output_rate_usd_per_million,
                        existing.price_context_multiplier,
                        existing.output_price_context_multiplier,
                        existing.estimated_cost_usd,
                        existing.pricing_status,
                        existing.pricing_missing_categories,
                        existing.dedup_key AS match_dedup_key,
                        1 AS match_priority
                    FROM usage_events AS existing
                    WHERE existing.agent_id = staged.agent_id
                      AND existing.source_instance_id = staged.source_instance_id
                      AND existing.source_entity_id = staged.source_entity_id
                      AND existing.event_id = staged.event_id
                      AND existing.pricing_status <> 0

                    UNION ALL

                    SELECT
                        existing.price_catalog_version,
                        existing.price_rule_id,
                        existing.input_rate_usd_per_million,
                        existing.cached_input_rate_usd_per_million,
                        existing.cache_write_rate_usd_per_million,
                        existing.output_rate_usd_per_million,
                        existing.price_context_multiplier,
                        existing.output_price_context_multiplier,
                        existing.estimated_cost_usd,
                        existing.pricing_status,
                        existing.pricing_missing_categories,
                        existing.dedup_key AS match_dedup_key,
                        2 AS match_priority
                    FROM usage_events AS existing
                    WHERE existing.agent_id = staged.agent_id
                      AND existing.source_instance_id = staged.source_instance_id
                      AND existing.source_entity_id = staged.source_entity_id
                      AND existing.source_revision = staged.source_revision
                      AND existing.pricing_status <> 0
                ) AS matched
                ORDER BY matched.match_priority, matched.match_dedup_key
                LIMIT 1
            )
            WHERE staged.agent_id = $agent_id
              AND staged.source_instance_id = $source_instance_id
              AND (
                  EXISTS (
                      SELECT 1
                      FROM usage_events AS existing
                      WHERE existing.agent_id = staged.agent_id
                        AND existing.source_instance_id = staged.source_instance_id
                        AND existing.dedup_key = staged.dedup_key
                        AND existing.pricing_status <> 0
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM usage_events AS existing
                      WHERE existing.agent_id = staged.agent_id
                        AND existing.source_instance_id = staged.source_instance_id
                        AND existing.source_entity_id = staged.source_entity_id
                        AND existing.event_id = staged.event_id
                        AND existing.pricing_status <> 0
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM usage_events AS existing
                      WHERE existing.agent_id = staged.agent_id
                        AND existing.source_instance_id = staged.source_instance_id
                        AND existing.source_entity_id = staged.source_entity_id
                        AND existing.source_revision = staged.source_revision
                        AND existing.pricing_status <> 0
                  )
              );
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergeStagedEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM usage_events AS existing
            WHERE existing.agent_id = $agent_id
              AND existing.source_instance_id = $source_instance_id
              AND (
                  EXISTS (
                      SELECT 1
                      FROM rebuild_stage.usage_events AS staged
                      WHERE staged.agent_id = existing.agent_id
                        AND staged.source_instance_id = existing.source_instance_id
                        AND staged.source_entity_id = existing.source_entity_id
                        AND staged.event_id = existing.event_id
                        AND staged.dedup_key <> existing.dedup_key
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM rebuild_stage.usage_events AS staged
                      WHERE staged.agent_id = existing.agent_id
                        AND staged.source_instance_id = existing.source_instance_id
                        AND staged.source_entity_id = existing.source_entity_id
                        AND staged.source_revision = existing.source_revision
                        AND staged.dedup_key <> existing.dedup_key
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM rebuild_stage.source_cursors AS staged_cursor
                      WHERE staged_cursor.source_instance_id =
                            existing.source_instance_id
                        AND staged_cursor.source_entity_id =
                            existing.source_entity_id
                        AND staged_cursor.event_revision_high_watermark
                            IS NOT NULL
                        AND existing.source_revision <=
                            staged_cursor.event_revision_high_watermark
                        AND NOT EXISTS (
                            SELECT 1
                            FROM rebuild_stage.usage_events AS staged
                            WHERE staged.agent_id = existing.agent_id
                              AND staged.source_instance_id =
                                  existing.source_instance_id
                              AND staged.source_entity_id =
                                  existing.source_entity_id
                              AND staged.source_revision =
                                  existing.source_revision
                        )
                  )
              );

            INSERT OR REPLACE INTO usage_events
            SELECT *
            FROM rebuild_stage.usage_events
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergeStagedCursorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO source_cursors (
                source_instance_id,
                source_entity_id,
                source_path,
                cursor_json,
                source_fingerprint,
                parser_version,
                event_revision_high_watermark,
                last_success_unix_ms,
                last_error,
                last_error_unix_ms
            )
            SELECT
                source_instance_id,
                source_entity_id,
                source_path,
                cursor_json,
                source_fingerprint,
                parser_version,
                event_revision_high_watermark,
                last_success_unix_ms,
                last_error,
                last_error_unix_ms
            FROM rebuild_stage.source_cursors
            WHERE source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteMissingStagedCursorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM source_cursors AS existing
            WHERE existing.source_instance_id = $source_instance_id
              AND NOT EXISTS (
                  SELECT 1
                  FROM rebuild_stage.source_cursors AS staged
                  WHERE staged.source_instance_id = existing.source_instance_id
                    AND staged.source_entity_id = existing.source_entity_id
              );
            """;
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<UsageSessionMetadata>>
        ReadStagedSessionsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SourceInstanceDescriptor instance,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                agent_id,
                source_instance_id,
                source_entity_id,
                session_id,
                session_kind,
                direct_parent_session_id,
                forked_from_session_id,
                relation_origin,
                relation_state,
                replay_state,
                compatibility_level,
                session_role,
                agent_path_hash,
                agent_leaf_hash,
                project_id,
                project_path,
                project_repository_hash,
                session_name,
                session_name_updated_unix_ms,
                last_observed_unix_ms,
                parser_version
            FROM rebuild_stage.usage_sessions
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id
            ORDER BY session_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);

        var sessions = new List<UsageSessionMetadata>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new UsageSessionMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                (SessionKind)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                (SessionRelationOrigin)reader.GetInt32(7),
                (SessionRelationState)reader.GetInt32(8),
                (ReplayState)reader.GetInt32(9),
                (CompatibilityLevel)reader.GetInt32(10),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19)),
                reader.GetString(20))
            {
                SessionRole = (SessionRole)reader.GetInt32(11),
                AgentPathHash = reader.IsDBNull(12) ? null : reader.GetString(12),
                AgentLeafHash = reader.IsDBNull(13) ? null : reader.GetString(13),
                ProjectId = reader.IsDBNull(14) ? null : reader.GetString(14),
                ProjectPath = reader.IsDBNull(15) ? null : reader.GetString(15),
                ProjectRepositoryIdentityHash =
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                SessionName = reader.IsDBNull(17) ? null : reader.GetString(17),
                SessionNameUpdatedAtUtc = reader.IsDBNull(18)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18))
            });
        }

        return sessions;
    }

    private static async Task UpdateCompatibilityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CompatibilityLevel compatibilityLevel,
        string? compatibilityCode,
        bool requiresRescan,
        string? acceptedParserVersion,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE source_instances
            SET compatibility_level = $compatibility_level,
                compatibility_code = $compatibility_code,
                requires_rescan = $requires_rescan,
                accepted_parser_version = COALESCE(
                    $accepted_parser_version,
                    accepted_parser_version)
            WHERE source_instance_id = $source_instance_id
              AND agent_id = $agent_id;
            """;
        command.Parameters.AddWithValue(
            "$compatibility_level",
            (int)compatibilityLevel);
        command.Parameters.AddWithValue(
            "$compatibility_code",
            (object?)compatibilityCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$requires_rescan",
            requiresRescan ? 1 : 0);
        command.Parameters.AddWithValue(
            "$accepted_parser_version",
            (object?)acceptedParserVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateAcceptedParserVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Accepted parser version is invalid.",
                nameof(value));
        }
    }

    private static void ValidateCompatibilityCode(string? value)
    {
        if (value is { Length: > 96 } ||
            value?.Any(static character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) is true)
        {
            throw new ArgumentException(
                "Compatibility code is invalid.",
                nameof(value));
        }
    }

    private static async Task UpdateObservedCompatibilityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageEventBatch batch,
        CancellationToken cancellationToken)
    {
        CompatibilityLevel observed = batch.Sessions.Count == 0
            ? CompatibilityLevel.FullyCompatible
            : batch.Sessions.Max(static value => value.CompatibilityLevel);
        if (observed == CompatibilityLevel.FullyCompatible)
        {
            return;
        }

        string code = observed switch
        {
            CompatibilityLevel.PartiallyCompatible =>
                "session_metadata_partial",
            CompatibilityLevel.TemporarilyIncompatible =>
                "source_temporarily_incompatible",
            CompatibilityLevel.MissingCapability =>
                "source_capability_missing",
            _ => throw new InvalidOperationException(
                "Unsupported compatibility level.")
        };
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE source_instances
            SET compatibility_code = CASE
                    WHEN compatibility_level < $compatibility_level
                    THEN $compatibility_code
                    ELSE compatibility_code
                END,
                compatibility_level = MAX(
                    compatibility_level,
                    $compatibility_level)
            WHERE source_instance_id = $source_instance_id
              AND agent_id = $agent_id;
            """;
        command.Parameters.AddWithValue(
            "$compatibility_level",
            (int)observed);
        command.Parameters.AddWithValue("$compatibility_code", code);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            batch.Instance.SourceInstanceId);
        command.Parameters.AddWithValue("$agent_id", batch.Instance.AgentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSourceInstanceDerivedDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM usage_events
            WHERE source_instance_id = $source_instance_id;

            DELETE FROM usage_turn_attributions
            WHERE source_instance_id = $source_instance_id;

            DELETE FROM usage_turn_dispatches
            WHERE source_instance_id = $source_instance_id;

            DELETE FROM usage_turns
            WHERE source_instance_id = $source_instance_id;

            DELETE FROM usage_sessions
            WHERE source_instance_id = $source_instance_id;

            DELETE FROM source_cursors
            WHERE source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAllStatisticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM usage_event_tools;
            DELETE FROM usage_turn_attributions;
            DELETE FROM usage_turn_dispatches;
            DELETE FROM usage_turns;
            DELETE FROM usage_sessions;
            DELETE FROM usage_events;
            DELETE FROM source_cursors;
            DELETE FROM source_instances;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyStagedCursorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_cursors (
                source_instance_id,
                source_entity_id,
                source_path,
                cursor_json,
                source_fingerprint,
                parser_version,
                event_revision_high_watermark,
                last_success_unix_ms,
                last_error,
                last_error_unix_ms
            )
            SELECT
                source_instance_id,
                source_entity_id,
                source_path,
                cursor_json,
                source_fingerprint,
                parser_version,
                event_revision_high_watermark,
                last_success_unix_ms,
                last_error,
                last_error_unix_ms
            FROM rebuild_stage.source_cursors
            WHERE source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyStagedEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_events
            SELECT *
            FROM rebuild_stage.usage_events
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyStagedSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_sessions
            SELECT *
            FROM rebuild_stage.usage_sessions
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyStagedTurnMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_turns
            SELECT *
            FROM rebuild_stage.usage_turns
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;

            INSERT INTO usage_event_tools
            SELECT *
            FROM rebuild_stage.usage_event_tools
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;

            INSERT INTO usage_turn_dispatches
            SELECT *
            FROM rebuild_stage.usage_turn_dispatches
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;

            INSERT INTO usage_turn_attributions
            SELECT *
            FROM rebuild_stage.usage_turn_attributions
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MergeStagedTurnMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM usage_turn_dispatches AS existing
            WHERE existing.agent_id = $agent_id
              AND existing.source_instance_id = $source_instance_id
              AND EXISTS (
                  SELECT 1
                  FROM rebuild_stage.source_cursors AS staged_source
                  WHERE staged_source.source_instance_id =
                        existing.source_instance_id
                    AND staged_source.source_entity_id =
                        existing.source_entity_id
              );

            DELETE FROM usage_turns AS existing
            WHERE existing.agent_id = $agent_id
              AND existing.source_instance_id = $source_instance_id
              AND EXISTS (
                  SELECT 1
                  FROM rebuild_stage.source_cursors AS staged_source
                  WHERE staged_source.source_instance_id =
                        existing.source_instance_id
                    AND staged_source.source_entity_id =
                        existing.source_entity_id
              );

            INSERT INTO usage_turns
            SELECT *
            FROM rebuild_stage.usage_turns
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id
            ON CONFLICT(
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash) DO UPDATE SET
                source_entity_id = excluded.source_entity_id,
                started_at_unix_ms = MIN(
                    usage_turns.started_at_unix_ms,
                    excluded.started_at_unix_ms),
                completed_at_unix_ms = CASE
                    WHEN usage_turns.completed_at_unix_ms IS NULL
                    THEN excluded.completed_at_unix_ms
                    WHEN excluded.completed_at_unix_ms IS NULL
                    THEN usage_turns.completed_at_unix_ms
                    ELSE MAX(
                        usage_turns.completed_at_unix_ms,
                        excluded.completed_at_unix_ms)
                END,
                prompt_preview = COALESCE(
                    excluded.prompt_preview,
                    usage_turns.prompt_preview),
                user_message_count = MAX(
                    usage_turns.user_message_count,
                    excluded.user_message_count),
                parser_version = excluded.parser_version,
                prompt_origin_turn_id_hash = COALESCE(
                    excluded.prompt_origin_turn_id_hash,
                    usage_turns.prompt_origin_turn_id_hash);

            DELETE FROM usage_event_tools AS existing
            WHERE existing.agent_id = $agent_id
              AND existing.source_instance_id = $source_instance_id
              AND EXISTS (
                  SELECT 1
                  FROM rebuild_stage.usage_events AS staged
                  WHERE staged.agent_id = existing.agent_id
                    AND staged.source_instance_id = existing.source_instance_id
                    AND staged.dedup_key = existing.event_dedup_key
              );

            INSERT INTO usage_event_tools
            SELECT *
            FROM rebuild_stage.usage_event_tools
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;

            INSERT OR REPLACE INTO usage_turn_dispatches
            SELECT *
            FROM rebuild_stage.usage_turn_dispatches
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id;
            """;
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageSessionMetadata> sessions,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UsageSessionSql.Upsert;

        foreach (UsageSessionMetadata value in sessions)
        {
            UsageSessionMetadata safeValue = await WouldCreateCycleAsync(
                connection,
                transaction,
                value,
                cancellationToken)
                ? WithUncertainRelation(value)
                : value;
            UsageSessionSql.Bind(command, safeValue);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertTurnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageTurnMetadata> turns,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UsageTurnSql.UpsertTurn;
        foreach (UsageTurnMetadata value in turns)
        {
            UsageTurnSql.BindTurn(command, value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertEventToolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageEventToolMetadata> tools,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UsageTurnSql.UpsertTool;
        foreach (UsageEventToolMetadata value in tools)
        {
            UsageTurnSql.BindTool(command, value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertDispatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageTurnDispatch> dispatches,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UsageTurnSql.UpsertDispatch;
        foreach (UsageTurnDispatch value in dispatches)
        {
            UsageTurnSql.BindDispatch(command, value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RebuildTurnAttributionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string sourceInstanceId,
        CancellationToken cancellationToken)
    {
        var sessions = new Dictionary<string, AttributionSessionRow>(
            StringComparer.Ordinal);
        var turns = new List<AttributionTurnRow>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    usage_sessions.session_id,
                    usage_sessions.direct_parent_session_id,
                    usage_sessions.relation_state,
                    usage_sessions.relation_origin,
                    usage_sessions.session_role,
                    usage_sessions.agent_path_hash,
                    usage_sessions.agent_leaf_hash,
                    usage_turns.turn_id_hash,
                    usage_turns.started_at_unix_ms,
                    usage_turns.completed_at_unix_ms,
                    usage_turns.prompt_origin_turn_id_hash
                FROM usage_turns
                INNER JOIN usage_sessions
                    ON usage_sessions.agent_id = usage_turns.agent_id
                   AND usage_sessions.source_instance_id =
                       usage_turns.source_instance_id
                   AND usage_sessions.session_id = usage_turns.session_id
                WHERE usage_turns.agent_id = $agent_id
                  AND usage_turns.source_instance_id = $source_instance_id
                ORDER BY usage_turns.started_at_unix_ms,
                         usage_turns.session_id,
                         usage_turns.turn_id_hash;
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.Parameters.AddWithValue(
                "$source_instance_id",
                sourceInstanceId);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string sessionId = reader.GetString(0);
                var session = new AttributionSessionRow(
                    sessionId,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    (SessionRelationState)reader.GetInt32(2),
                    (SessionRelationOrigin)reader.GetInt32(3),
                    (SessionRole)reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
                sessions[sessionId] = session;
                turns.Add(new AttributionTurnRow(
                    sessionId,
                    reader.GetString(7),
                    reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        var dispatches = new List<AttributionDispatchRow>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    source_session_id,
                    source_turn_id_hash,
                    target_agent_hash,
                    dispatch_kind,
                    target_kind,
                    occurred_at_unix_ms
                FROM usage_turn_dispatches
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id
                ORDER BY occurred_at_unix_ms, dispatch_id_hash;
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.Parameters.AddWithValue(
                "$source_instance_id",
                sourceInstanceId);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dispatches.Add(new AttributionDispatchRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (TurnDispatchKind)reader.GetInt32(3),
                    (DispatchTargetKind)reader.GetInt32(4),
                    reader.GetInt64(5)));
            }
        }

        Dictionary<string, AttributionTurnRow[]> turnsBySession = turns
            .GroupBy(static value => value.SessionId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static value => value.StartedAtUnixMs)
                    .ThenBy(static value => value.TurnIdHash, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var resolved = new Dictionary<AttributionTurnKey, AttributionValue>();
        foreach (AttributionTurnRow turn in turns)
        {
            AttributionSessionRow session = sessions[turn.SessionId];
            if ((session.RelationState is not SessionRelationState.Confirmed ||
                 session.ParentSessionId is null) &&
                turn.PromptOriginTurnIdHash is null)
            {
                resolved[new AttributionTurnKey(turn.SessionId, turn.TurnIdHash)] =
                    new AttributionValue(
                        turn.SessionId,
                        turn.TurnIdHash,
                        TurnAttributionOrigin.Direct);
            }
        }

        for (int pass = 0; pass < Math.Max(1, turns.Count); pass++)
        {
            bool changed = false;
            foreach (AttributionTurnRow turn in turns)
            {
                var key = new AttributionTurnKey(turn.SessionId, turn.TurnIdHash);
                if (resolved.ContainsKey(key))
                {
                    continue;
                }

                if (turn.PromptOriginTurnIdHash is not null)
                {
                    var target = new AttributionTurnKey(
                        turn.SessionId,
                        turn.PromptOriginTurnIdHash);
                    bool targetExists = turnsBySession[turn.SessionId].Any(value =>
                        string.Equals(
                            value.TurnIdHash,
                            turn.PromptOriginTurnIdHash,
                            StringComparison.Ordinal));
                    if (targetExists &&
                        !string.Equals(
                            turn.TurnIdHash,
                            turn.PromptOriginTurnIdHash,
                            StringComparison.Ordinal) &&
                        resolved.TryGetValue(target, out AttributionValue? origin) &&
                        origin is not null)
                    {
                        resolved[key] = new AttributionValue(
                            origin.OriginSessionId,
                            origin.OriginTurnIdHash,
                            TurnAttributionOrigin.GoalContinuation);
                        changed = true;
                    }

                    continue;
                }

                AttributionSessionRow session = sessions[turn.SessionId];
                if (session.RelationState is not SessionRelationState.Confirmed ||
                    session.ParentSessionId is null)
                {
                    continue;
                }

                (AttributionTurnKey Parent, TurnAttributionOrigin Origin)? parent =
                    session.Role switch
                    {
                        SessionRole.Subagent => ResolveSubagentParent(
                            turn,
                            session,
                            turnsBySession[turn.SessionId],
                            turnsBySession,
                            dispatches),
                        SessionRole.Guardian or SessionRole.Internal =>
                            ResolveIntervalParent(
                                turn,
                                session.ParentSessionId,
                                turnsBySession,
                                TurnAttributionOrigin.GuardianInterval),
                        _ => null
                    };
                if (!parent.HasValue ||
                    !resolved.TryGetValue(
                        parent.Value.Parent,
                        out AttributionValue? parentOrigin) ||
                    parentOrigin is null)
                {
                    continue;
                }

                resolved[key] = new AttributionValue(
                    parentOrigin.OriginSessionId,
                    parentOrigin.OriginTurnIdHash,
                    parent.Value.Origin);
                changed = true;
            }

            if (!changed)
            {
                break;
            }
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM usage_turn_attributions
                WHERE agent_id = $agent_id
                  AND source_instance_id = $source_instance_id;
                """;
            delete.Parameters.AddWithValue("$agent_id", agentId);
            delete.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO usage_turn_attributions (
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash,
                origin_session_id,
                origin_turn_id_hash,
                attribution_origin,
                attribution_state
            ) VALUES (
                $agent_id,
                $source_instance_id,
                $session_id,
                $turn_id_hash,
                $origin_session_id,
                $origin_turn_id_hash,
                $attribution_origin,
                $attribution_state
            );
            """;
        foreach (AttributionTurnRow turn in turns)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$agent_id", agentId);
            insert.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
            insert.Parameters.AddWithValue("$session_id", turn.SessionId);
            insert.Parameters.AddWithValue("$turn_id_hash", turn.TurnIdHash);
            var key = new AttributionTurnKey(turn.SessionId, turn.TurnIdHash);
            if (resolved.TryGetValue(
                    key,
                    out AttributionValue? attribution) &&
                attribution is not null)
            {
                insert.Parameters.AddWithValue(
                    "$origin_session_id",
                    attribution.OriginSessionId);
                insert.Parameters.AddWithValue(
                    "$origin_turn_id_hash",
                    attribution.OriginTurnIdHash);
                insert.Parameters.AddWithValue(
                    "$attribution_origin",
                    (int)attribution.Origin);
                insert.Parameters.AddWithValue(
                    "$attribution_state",
                    (int)TurnAttributionState.Confirmed);
            }
            else
            {
                insert.Parameters.AddWithValue(
                    "$origin_session_id",
                    DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$origin_turn_id_hash",
                    DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$attribution_origin",
                    DBNull.Value);
                insert.Parameters.AddWithValue(
                    "$attribution_state",
                    (int)TurnAttributionState.Uncertain);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static (
        AttributionTurnKey Parent,
        TurnAttributionOrigin Origin)? ResolveSubagentParent(
        AttributionTurnRow turn,
        AttributionSessionRow session,
        AttributionTurnRow[] sessionTurns,
        IReadOnlyDictionary<string, AttributionTurnRow[]> turnsBySession,
        IReadOnlyList<AttributionDispatchRow> dispatches)
    {
        (AttributionTurnKey Parent, TurnAttributionOrigin Origin)? dispatched =
            ResolveDispatchedParent(turn, session, sessionTurns, dispatches);
        if (dispatched.HasValue ||
            session.RelationOrigin is not SessionRelationOrigin.SourceAgentParent ||
            session.ParentSessionId is null)
        {
            return dispatched;
        }

        return ResolveIntervalParent(
            turn,
            session.ParentSessionId,
            turnsBySession,
            TurnAttributionOrigin.SourceParentInterval);
    }

    private static (
        AttributionTurnKey Parent,
        TurnAttributionOrigin Origin)? ResolveDispatchedParent(
        AttributionTurnRow turn,
        AttributionSessionRow session,
        AttributionTurnRow[] sessionTurns,
        IReadOnlyList<AttributionDispatchRow> dispatches)
    {
        int turnIndex = Array.IndexOf(sessionTurns, turn);
        TurnDispatchKind kind = turnIndex == 0
            ? TurnDispatchKind.Spawn
            : TurnDispatchKind.FollowUp;
        string? targetHash = kind is TurnDispatchKind.Spawn
            ? session.AgentLeafHash
            : session.AgentPathHash;
        DispatchTargetKind targetKind = kind is TurnDispatchKind.Spawn
            ? DispatchTargetKind.AgentLeaf
            : DispatchTargetKind.AgentPath;
        if (targetHash is null || session.ParentSessionId is null)
        {
            return null;
        }

        long lowerBound = turnIndex > 0
            ? sessionTurns[turnIndex - 1].CompletedAtUnixMs ??
              sessionTurns[turnIndex - 1].StartedAtUnixMs
            : long.MinValue;
        AttributionDispatchRow[] candidates = dispatches
            .Where(value =>
                value.Kind == kind &&
                value.TargetKind == targetKind &&
                string.Equals(
                    value.SourceSessionId,
                    session.ParentSessionId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    value.TargetAgentHash,
                    targetHash,
                    StringComparison.Ordinal) &&
                value.OccurredAtUnixMs >= lowerBound &&
                value.OccurredAtUnixMs <= turn.StartedAtUnixMs)
            .ToArray();
        return candidates.Length == 1
            ? (
                new AttributionTurnKey(
                    candidates[0].SourceSessionId,
                    candidates[0].SourceTurnIdHash),
                kind is TurnDispatchKind.Spawn
                    ? TurnAttributionOrigin.Spawn
                    : TurnAttributionOrigin.FollowUp)
            : null;
    }

    private static (
        AttributionTurnKey Parent,
        TurnAttributionOrigin Origin)? ResolveIntervalParent(
        AttributionTurnRow child,
        string parentSessionId,
        IReadOnlyDictionary<string, AttributionTurnRow[]> turnsBySession,
        TurnAttributionOrigin origin)
    {
        if (!turnsBySession.TryGetValue(
                parentSessionId,
                out AttributionTurnRow[]? parentTurns))
        {
            return null;
        }

        AttributionTurnRow[] candidates = parentTurns
            .Where(value =>
                value.StartedAtUnixMs <= child.StartedAtUnixMs &&
                value.CompletedAtUnixMs.HasValue &&
                value.CompletedAtUnixMs.Value >= child.StartedAtUnixMs)
            .ToArray();
        return candidates.Length == 1
            ? (
                new AttributionTurnKey(
                    candidates[0].SessionId,
                    candidates[0].TurnIdHash),
                origin)
            : null;
    }

    private static async Task<bool> WouldCreateCycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageSessionMetadata value,
        CancellationToken cancellationToken)
    {
        if (value.RelationState is not SessionRelationState.Confirmed ||
            value.DirectParentSessionId is null)
        {
            return false;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE ancestors(session_id, depth) AS (
                SELECT $parent_session_id, 0

                UNION ALL

                SELECT usage_sessions.direct_parent_session_id, ancestors.depth + 1
                FROM ancestors
                INNER JOIN usage_sessions
                    ON usage_sessions.agent_id = $agent_id
                   AND usage_sessions.source_instance_id = $source_instance_id
                   AND usage_sessions.session_id = ancestors.session_id
                WHERE usage_sessions.relation_state = 1
                  AND usage_sessions.direct_parent_session_id IS NOT NULL
                  AND ancestors.depth < 1024
            )
            SELECT EXISTS (
                SELECT 1
                FROM ancestors
                WHERE session_id = $session_id
            );
            """;
        command.Parameters.AddWithValue("$agent_id", value.AgentId);
        command.Parameters.AddWithValue(
            "$source_instance_id",
            value.SourceInstanceId);
        command.Parameters.AddWithValue("$session_id", value.SessionId);
        command.Parameters.AddWithValue(
            "$parent_session_id",
            value.DirectParentSessionId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result) != 0;
    }

    private static UsageSessionMetadata WithUncertainRelation(
        UsageSessionMetadata value) => new(
            value.AgentId,
            value.SourceInstanceId,
            value.SourceEntityId,
            value.SessionId,
            value.SessionKind,
            null,
            value.ForkedFromSessionId,
            SessionRelationOrigin.None,
            SessionRelationState.Uncertain,
            value.ReplayState,
            CompatibilityLevel.PartiallyCompatible,
            value.ObservedAtUtc,
            value.ParserVersion)
        {
            ProjectId = value.ProjectId,
            ProjectPath = value.ProjectPath,
            ProjectRepositoryIdentityHash =
                value.ProjectRepositoryIdentityHash,
            SessionRole = value.SessionRole,
            AgentPathHash = value.AgentPathHash,
            AgentLeafHash = value.AgentLeafHash,
            SessionName = value.SessionName,
            SessionNameUpdatedAtUtc = value.SessionNameUpdatedAtUtc
        };

    private static async Task UpsertInstanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceInstanceDescriptor instance,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertInstance;
        BindInstance(command, instance);
        command.Parameters.AddWithValue(
            "$checked_at",
            checkedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSuccessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UsageEventBatch batch,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSuccessfulCursor;
        BindEntity(command, batch.Entity);
        command.Parameters.AddWithValue("$cursor_json", batch.CursorJson);
        command.Parameters.AddWithValue(
            "$source_fingerprint",
            batch.SourceFingerprint);
        command.Parameters.AddWithValue("$parser_version", batch.ParserVersion);
        command.Parameters.AddWithValue(
            "$event_revision_high_watermark",
            (object?)batch.EventRevisionHighWatermark ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$checked_at",
            batch.CheckedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindInstance(
        SqliteCommand command,
        SourceInstanceDescriptor instance)
    {
        command.Parameters.AddWithValue(
            "$source_instance_id",
            instance.SourceInstanceId);
        command.Parameters.AddWithValue("$agent_id", instance.AgentId);
        command.Parameters.AddWithValue("$source_kind", (int)instance.SourceKind);
        command.Parameters.AddWithValue("$display_name", instance.DisplayName);
        command.Parameters.AddWithValue("$root_path", instance.RootPath);
    }

    private static void BindEntity(
        SqliteCommand command,
        SourceEntityDescriptor entity)
    {
        command.Parameters.AddWithValue(
            "$source_instance_id",
            entity.SourceInstanceId);
        command.Parameters.AddWithValue("$source_entity_id", entity.SourceEntityId);
        command.Parameters.AddWithValue("$source_path", entity.SourcePath);
    }

    private static void ValidateBatch(UsageEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Instance);
        ArgumentNullException.ThrowIfNull(batch.Entity);
        ArgumentNullException.ThrowIfNull(batch.Events);
        ArgumentNullException.ThrowIfNull(batch.Sessions);
        ArgumentNullException.ThrowIfNull(batch.Turns);
        ArgumentNullException.ThrowIfNull(batch.EventTools);
        ArgumentNullException.ThrowIfNull(batch.Dispatches);
        ValidateInstance(batch.Instance);
        ValidateEntity(batch.Entity, batch.Instance.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.CursorJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.SourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.ParserVersion);
        ValidateUtc(batch.CheckedAtUtc, nameof(batch.CheckedAtUtc));

        if (!Enum.IsDefined(batch.Intent))
        {
            throw new ArgumentOutOfRangeException(nameof(batch), "未知的写入意图。");
        }

        if (batch.EventRevisionHighWatermark is < 0 ||
            batch.Events.Any(value =>
                batch.EventRevisionHighWatermark.HasValue &&
                value.SourceRevision > batch.EventRevisionHighWatermark.Value))
        {
            throw new ArgumentException(
                "事件修订高水位必须覆盖批次中的所有事件。",
                nameof(batch));
        }

        foreach (UsageEvent value in batch.Events)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!string.Equals(
                    value.SourceInstanceId,
                    batch.Instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceEntityId,
                    batch.Entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.AgentId,
                    batch.Instance.AgentId,
                    StringComparison.Ordinal) ||
                value.SourceKind != batch.Instance.SourceKind ||
                !string.Equals(
                    value.ParserVersion,
                    batch.ParserVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceFingerprint,
                    batch.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "批次中的事件必须与来源身份、指纹和 Parser 版本一致。",
                    nameof(batch));
            }
        }

        foreach (UsageSessionMetadata value in batch.Sessions)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!string.Equals(
                    value.SourceInstanceId,
                    batch.Instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.SourceEntityId,
                    batch.Entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.AgentId,
                    batch.Instance.AgentId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    value.ParserVersion,
                    batch.ParserVersion,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "批次中的会话元数据必须与来源身份和 Parser 版本一致。",
                    nameof(batch));
            }

            if (!Enum.IsDefined(value.SessionRole))
            {
                throw new ArgumentException("批次中的会话角色无效。", nameof(batch));
            }
        }

        ValidateDerivedBatch(
            batch.Turns,
            batch,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
        ValidateDerivedBatch(
            batch.EventTools,
            batch,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
        ValidateDerivedBatch(
            batch.Dispatches,
            batch,
            static value => (
                value.AgentId,
                value.SourceInstanceId,
                value.SourceEntityId,
                value.ParserVersion));
    }

    private static void ValidateDerivedBatch<T>(
        IReadOnlyList<T> values,
        UsageEventBatch batch,
        Func<T, (string AgentId, string InstanceId, string EntityId, string ParserVersion)>
            identity)
    {
        foreach (T value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            (string agentId, string instanceId, string entityId, string parserVersion) =
                identity(value);
            if (!string.Equals(agentId, batch.Instance.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    instanceId,
                    batch.Instance.SourceInstanceId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    entityId,
                    batch.Entity.SourceEntityId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    parserVersion,
                    batch.ParserVersion,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "批次中的派生元数据必须与来源身份和 Parser 版本一致。",
                    nameof(batch));
            }
        }
    }

    private static void ValidateInstance(SourceInstanceDescriptor instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.RootPath);

        if (!Enum.IsDefined(instance.SourceKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(instance),
                "未知的来源存储类型。");
        }
    }

    private static void ValidateEntity(
        SourceEntityDescriptor entity,
        string expectedInstanceId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.SourceInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.SourceEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.SourcePath);

        if (!string.Equals(
                entity.SourceInstanceId,
                expectedInstanceId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "来源实体必须属于同一来源实例。",
                nameof(entity));
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("时间必须使用 UTC。", parameterName);
        }
    }

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int index) =>
        reader.IsDBNull(index)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));

    private sealed record AttributionSessionRow(
        string SessionId,
        string? ParentSessionId,
        SessionRelationState RelationState,
        SessionRelationOrigin RelationOrigin,
        SessionRole Role,
        string? AgentPathHash,
        string? AgentLeafHash);

    private sealed record AttributionTurnRow(
        string SessionId,
        string TurnIdHash,
        long StartedAtUnixMs,
        long? CompletedAtUnixMs,
        string? PromptOriginTurnIdHash);

    private sealed record AttributionDispatchRow(
        string SourceSessionId,
        string SourceTurnIdHash,
        string TargetAgentHash,
        TurnDispatchKind Kind,
        DispatchTargetKind TargetKind,
        long OccurredAtUnixMs);

    private readonly record struct AttributionTurnKey(
        string SessionId,
        string TurnIdHash);

    private sealed record AttributionValue(
        string OriginSessionId,
        string OriginTurnIdHash,
        TurnAttributionOrigin Origin);
}
