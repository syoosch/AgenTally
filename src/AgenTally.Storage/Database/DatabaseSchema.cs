using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Database;

public static class DatabaseSchemaInfo
{
    public const int CurrentVersion = 14;
}

internal static class DatabaseSchema
{
    internal const int CurrentVersion = DatabaseSchemaInfo.CurrentVersion;

    private const string CreateV14 = """
        CREATE TABLE schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY,
            applied_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE source_instances (
            source_instance_id TEXT NOT NULL PRIMARY KEY,
            agent_id TEXT NOT NULL,
            source_kind INTEGER NOT NULL,
            display_name TEXT NOT NULL,
            root_path TEXT NOT NULL,
            last_checked_unix_ms INTEGER NOT NULL,
            compatibility_level INTEGER NOT NULL DEFAULT 0,
            compatibility_code TEXT NULL,
            requires_rescan INTEGER NOT NULL DEFAULT 0,
            accepted_parser_version TEXT NULL
        );

        CREATE TABLE source_cursors (
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            source_path TEXT NOT NULL,
            cursor_json TEXT NULL,
            source_fingerprint TEXT NULL,
            parser_version TEXT NULL,
            event_revision_high_watermark INTEGER NULL,
            last_success_unix_ms INTEGER NULL,
            last_error TEXT NULL,
            last_error_unix_ms INTEGER NULL,
            PRIMARY KEY (source_instance_id, source_entity_id),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_events (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            event_id TEXT NOT NULL,
            dedup_key TEXT NOT NULL,
            source_kind INTEGER NOT NULL,
            occurred_at_unix_ms INTEGER NOT NULL,
            imported_at_unix_ms INTEGER NOT NULL,
            session_id TEXT NULL,
            parent_session_id TEXT NULL,
            project_id TEXT NULL,
            raw_model TEXT NULL,
            normalized_model TEXT NULL,
            provider_id TEXT NULL,
            model_resolution_origin INTEGER NOT NULL,
            input_reported_value INTEGER NULL,
            input_reported_origin INTEGER NOT NULL,
            uncached_input_value INTEGER NULL,
            uncached_input_origin INTEGER NOT NULL,
            cache_read_value INTEGER NULL,
            cache_read_origin INTEGER NOT NULL,
            cache_write_value INTEGER NULL,
            cache_write_origin INTEGER NOT NULL,
            output_value INTEGER NULL,
            output_origin INTEGER NOT NULL,
            reasoning_value INTEGER NULL,
            reasoning_origin INTEGER NOT NULL,
            tool_value INTEGER NULL,
            tool_origin INTEGER NOT NULL,
            reported_total_value INTEGER NULL,
            reported_total_origin INTEGER NOT NULL,
            normalized_total_value INTEGER NULL,
            normalized_total_origin INTEGER NOT NULL,
            cache_included_in_input INTEGER NOT NULL,
            reasoning_included_in_output INTEGER NOT NULL,
            completion_state INTEGER NOT NULL,
            data_quality INTEGER NOT NULL,
            reported_cost TEXT NULL,
            currency TEXT NULL,
            parser_version TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL,
            source_revision INTEGER NOT NULL,
            project_path TEXT NULL,
            turn_id_hash TEXT NULL,
            price_catalog_version TEXT NULL,
            price_rule_id TEXT NULL,
            input_rate_usd_per_million TEXT NULL,
            cached_input_rate_usd_per_million TEXT NULL,
            cache_write_rate_usd_per_million TEXT NULL,
            output_rate_usd_per_million TEXT NULL,
            price_context_multiplier TEXT NULL,
            output_price_context_multiplier TEXT NULL,
            estimated_cost_usd TEXT NULL,
            pricing_status INTEGER NOT NULL DEFAULT 0,
            pricing_missing_categories INTEGER NOT NULL DEFAULT 0,
            project_repository_hash TEXT NULL,
            route_model_id TEXT NULL,
            model_display_name TEXT NULL,
            PRIMARY KEY (agent_id, source_instance_id, dedup_key),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_sessions (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            session_kind INTEGER NOT NULL,
            direct_parent_session_id TEXT NULL,
            forked_from_session_id TEXT NULL,
            relation_origin INTEGER NOT NULL,
            relation_state INTEGER NOT NULL,
            replay_state INTEGER NOT NULL,
            compatibility_level INTEGER NOT NULL,
            session_role INTEGER NOT NULL DEFAULT 0,
            agent_path_hash TEXT NULL,
            agent_leaf_hash TEXT NULL,
            project_id TEXT NULL,
            project_path TEXT NULL,
            first_observed_unix_ms INTEGER NOT NULL,
            last_observed_unix_ms INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            project_repository_hash TEXT NULL,
            session_name TEXT NULL,
            session_name_updated_unix_ms INTEGER NULL,
            PRIMARY KEY (agent_id, source_instance_id, session_id),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_turns (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            turn_id_hash TEXT NOT NULL,
            started_at_unix_ms INTEGER NOT NULL,
            completed_at_unix_ms INTEGER NULL,
            prompt_preview TEXT NULL,
            user_message_count INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            prompt_origin_turn_id_hash TEXT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_event_tools (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            event_dedup_key TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            tool_name TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                event_dedup_key,
                ordinal),
            FOREIGN KEY (
                agent_id,
                source_instance_id,
                event_dedup_key)
                REFERENCES usage_events(
                    agent_id,
                    source_instance_id,
                    dedup_key)
                ON DELETE CASCADE
        );

        CREATE TABLE usage_turn_dispatches (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            source_session_id TEXT NOT NULL,
            source_turn_id_hash TEXT NOT NULL,
            dispatch_id_hash TEXT NOT NULL,
            target_agent_hash TEXT NOT NULL,
            dispatch_kind INTEGER NOT NULL,
            target_kind INTEGER NOT NULL,
            occurred_at_unix_ms INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                dispatch_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_turn_attributions (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            turn_id_hash TEXT NOT NULL,
            origin_session_id TEXT NULL,
            origin_turn_id_hash TEXT NULL,
            attribution_origin INTEGER NULL,
            attribution_state INTEGER NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE pricing_overrides (
            normalized_model TEXT NOT NULL PRIMARY KEY,
            input_rate_usd_per_million TEXT NOT NULL,
            cached_input_rate_usd_per_million TEXT NULL,
            cache_write_rate_usd_per_million TEXT NULL,
            output_rate_usd_per_million TEXT NOT NULL,
            long_context_threshold_tokens INTEGER NULL,
            long_context_input_multiplier TEXT NOT NULL,
            long_context_output_multiplier TEXT NOT NULL,
            updated_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE pricing_catalog_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            catalog_version TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE model_identity_catalog_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            catalog_version TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL
        );

        CREATE INDEX ix_usage_events_occurred_at
            ON usage_events (occurred_at_unix_ms);

        CREATE INDEX ix_usage_events_agent_occurred_at
            ON usage_events (agent_id, occurred_at_unix_ms);

        CREATE INDEX ix_usage_events_model_occurred_at
            ON usage_events (normalized_model, occurred_at_unix_ms);

        CREATE INDEX ix_usage_events_session
            ON usage_events (session_id);

        CREATE INDEX ix_usage_events_session_occurred
            ON usage_events (session_id, occurred_at_unix_ms, dedup_key);

        CREATE INDEX ix_usage_events_project_occurred
            ON usage_events (project_id, occurred_at_unix_ms, dedup_key);

        CREATE INDEX ix_usage_events_project_path
            ON usage_events (project_path COLLATE NOCASE);

        CREATE INDEX ix_usage_events_turn
            ON usage_events (session_id, turn_id_hash);

        CREATE INDEX ix_usage_events_source_event
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                event_id);

        CREATE INDEX ix_usage_events_source_revision
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                source_revision);

        CREATE INDEX ix_usage_sessions_parent
            ON usage_sessions (source_instance_id, direct_parent_session_id);

        CREATE INDEX ix_usage_sessions_project
            ON usage_sessions (project_id, last_observed_unix_ms);

        CREATE INDEX ix_usage_sessions_project_path
            ON usage_sessions (project_path COLLATE NOCASE);

        CREATE INDEX ix_usage_turns_started
            ON usage_turns (
                source_instance_id,
                session_id,
                started_at_unix_ms,
                turn_id_hash);

        CREATE INDEX ix_usage_turn_dispatches_target
            ON usage_turn_dispatches (
                source_instance_id,
                target_agent_hash,
                occurred_at_unix_ms);

        CREATE INDEX ix_usage_turn_attributions_origin
            ON usage_turn_attributions (
                source_instance_id,
                origin_session_id,
                origin_turn_id_hash);

        INSERT INTO model_identity_catalog_state (
            singleton_id,
            catalog_version,
            applied_at_unix_ms
        ) VALUES (
            1,
            $model_catalog_version,
            $applied_at_unix_ms
        );

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (14, $applied_at_unix_ms);

        PRAGMA user_version = 14;
        """;

    private const string MigrateV2ToV3 = """
        ALTER TABLE usage_events
        ADD COLUMN project_path TEXT NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (3, $applied_at_unix_ms);

        PRAGMA user_version = 3;
        """;

    private const string MigrateV3ToV4 = """
        ALTER TABLE source_instances
        ADD COLUMN compatibility_level INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE source_instances
        ADD COLUMN compatibility_code TEXT NULL;

        ALTER TABLE source_instances
        ADD COLUMN requires_rescan INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE source_instances
        ADD COLUMN accepted_parser_version TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN turn_id_hash TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN price_catalog_version TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN price_rule_id TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN input_rate_usd_per_million TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN cached_input_rate_usd_per_million TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN cache_write_rate_usd_per_million TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN output_rate_usd_per_million TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN price_context_multiplier TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN output_price_context_multiplier TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN estimated_cost_usd TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN pricing_status INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE usage_events
        ADD COLUMN pricing_missing_categories INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE usage_sessions (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            session_kind INTEGER NOT NULL,
            direct_parent_session_id TEXT NULL,
            forked_from_session_id TEXT NULL,
            relation_origin INTEGER NOT NULL,
            relation_state INTEGER NOT NULL,
            replay_state INTEGER NOT NULL,
            compatibility_level INTEGER NOT NULL,
            project_id TEXT NULL,
            project_path TEXT NULL,
            first_observed_unix_ms INTEGER NOT NULL,
            last_observed_unix_ms INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (agent_id, source_instance_id, session_id),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE pricing_overrides (
            normalized_model TEXT NOT NULL PRIMARY KEY,
            input_rate_usd_per_million TEXT NOT NULL,
            cached_input_rate_usd_per_million TEXT NULL,
            cache_write_rate_usd_per_million TEXT NULL,
            output_rate_usd_per_million TEXT NOT NULL,
            long_context_threshold_tokens INTEGER NULL,
            long_context_input_multiplier TEXT NOT NULL,
            long_context_output_multiplier TEXT NOT NULL,
            updated_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE pricing_catalog_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            catalog_version TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL
        );

        CREATE INDEX ix_usage_events_session_occurred
            ON usage_events (session_id, occurred_at_unix_ms, dedup_key);

        CREATE INDEX ix_usage_events_project_occurred
            ON usage_events (project_id, occurred_at_unix_ms, dedup_key);

        CREATE INDEX ix_usage_events_turn
            ON usage_events (session_id, turn_id_hash);

        CREATE INDEX ix_usage_events_source_event
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                event_id);

        CREATE INDEX ix_usage_events_source_revision
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                source_revision);

        CREATE INDEX ix_usage_sessions_parent
            ON usage_sessions (source_instance_id, direct_parent_session_id);

        CREATE INDEX ix_usage_sessions_project
            ON usage_sessions (project_id, last_observed_unix_ms);

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (4, $applied_at_unix_ms);

        PRAGMA user_version = 4;
        """;

    private const string MigrateV4ToV5 = """
        ALTER TABLE usage_sessions
        ADD COLUMN session_role INTEGER NOT NULL DEFAULT 0;

        ALTER TABLE usage_sessions
        ADD COLUMN agent_path_hash TEXT NULL;

        ALTER TABLE usage_sessions
        ADD COLUMN agent_leaf_hash TEXT NULL;

        CREATE TABLE usage_turns (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            turn_id_hash TEXT NOT NULL,
            started_at_unix_ms INTEGER NOT NULL,
            completed_at_unix_ms INTEGER NULL,
            prompt_preview TEXT NULL,
            user_message_count INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_event_tools (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            event_dedup_key TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            tool_name TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                event_dedup_key,
                ordinal),
            FOREIGN KEY (
                agent_id,
                source_instance_id,
                event_dedup_key)
                REFERENCES usage_events(
                    agent_id,
                    source_instance_id,
                    dedup_key)
                ON DELETE CASCADE
        );

        CREATE TABLE usage_turn_dispatches (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            source_entity_id TEXT NOT NULL,
            source_session_id TEXT NOT NULL,
            source_turn_id_hash TEXT NOT NULL,
            dispatch_id_hash TEXT NOT NULL,
            target_agent_hash TEXT NOT NULL,
            dispatch_kind INTEGER NOT NULL,
            target_kind INTEGER NOT NULL,
            occurred_at_unix_ms INTEGER NOT NULL,
            parser_version TEXT NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                dispatch_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE TABLE usage_turn_attributions (
            agent_id TEXT NOT NULL,
            source_instance_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            turn_id_hash TEXT NOT NULL,
            origin_session_id TEXT NULL,
            origin_turn_id_hash TEXT NULL,
            attribution_origin INTEGER NULL,
            attribution_state INTEGER NOT NULL,
            PRIMARY KEY (
                agent_id,
                source_instance_id,
                session_id,
                turn_id_hash),
            FOREIGN KEY (source_instance_id)
                REFERENCES source_instances(source_instance_id)
        );

        CREATE INDEX ix_usage_turns_started
            ON usage_turns (
                source_instance_id,
                session_id,
                started_at_unix_ms,
                turn_id_hash);

        CREATE INDEX ix_usage_turn_dispatches_target
            ON usage_turn_dispatches (
                source_instance_id,
                target_agent_hash,
                occurred_at_unix_ms);

        CREATE INDEX ix_usage_turn_attributions_origin
            ON usage_turn_attributions (
                source_instance_id,
                origin_session_id,
                origin_turn_id_hash);

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (5, $applied_at_unix_ms);

        PRAGMA user_version = 5;
        """;

    private const string MigrateV5ToV6 = """
        ALTER TABLE usage_events
        ADD COLUMN project_repository_hash TEXT NULL;

        ALTER TABLE usage_sessions
        ADD COLUMN project_repository_hash TEXT NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (6, $applied_at_unix_ms);

        PRAGMA user_version = 6;
        """;

    private const string MigrateV6ToV7 = """
        ALTER TABLE usage_sessions
        ADD COLUMN session_name TEXT NULL;

        ALTER TABLE usage_sessions
        ADD COLUMN session_name_updated_unix_ms INTEGER NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (7, $applied_at_unix_ms);

        PRAGMA user_version = 7;
        """;

    private const string MigrateV7ToV8 = """
        ALTER TABLE source_cursors
        ADD COLUMN event_revision_high_watermark INTEGER NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (8, $applied_at_unix_ms);

        PRAGMA user_version = 8;
        """;

    private const string MigrateV8ToV9 = """
        CREATE INDEX IF NOT EXISTS ix_usage_events_project_path
            ON usage_events (project_path COLLATE NOCASE);

        CREATE INDEX IF NOT EXISTS ix_usage_sessions_project_path
            ON usage_sessions (project_path COLLATE NOCASE);

        CREATE TEMP TABLE unique_project_repositories (
            project_path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            repository_hash TEXT NOT NULL
        );

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
            FROM usage_events

            UNION ALL

            SELECT
                project_path,
                project_repository_hash AS repository_hash
            FROM usage_sessions
        ) AS candidate
        WHERE candidate.project_path IS NOT NULL
          AND LENGTH(candidate.repository_hash) = 64
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

        DROP TABLE unique_project_repositories;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (9, $applied_at_unix_ms);

        PRAGMA user_version = 9;
        """;

    private const string MigrateV9ToV10 = """
        ALTER TABLE usage_events
        ADD COLUMN route_model_id TEXT NULL;

        ALTER TABLE usage_events
        ADD COLUMN model_display_name TEXT NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (10, $applied_at_unix_ms);

        PRAGMA user_version = 10;
        """;

    private const string MigrateV10ToV11 = """
        ALTER TABLE usage_turns
        ADD COLUMN prompt_origin_turn_id_hash TEXT NULL;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (11, $applied_at_unix_ms);

        PRAGMA user_version = 11;
        """;

    private const string MigrateV11ToV12 = """
        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (12, $applied_at_unix_ms);

        PRAGMA user_version = 12;
        """;

    private const string MigrateV12ToV13 = """
        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (13, $applied_at_unix_ms);

        PRAGMA user_version = 13;
        """;

    private const string MigrateV13ToV14 = """
        CREATE TABLE model_identity_catalog_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            catalog_version TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL
        );

        INSERT INTO model_identity_catalog_state (
            singleton_id,
            catalog_version,
            applied_at_unix_ms
        ) VALUES (
            1,
            $model_catalog_version,
            $applied_at_unix_ms
        );

        DELETE FROM pricing_catalog_state;

        INSERT INTO schema_migrations (version, applied_at_unix_ms)
        VALUES (14, $applied_at_unix_ms);

        PRAGMA user_version = 14;
        """;

    public static async Task InitializeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int version = await GetUserVersionAsync(connection, cancellationToken);
        if (version == 0)
        {
            await ConfigureConnectionAsync(connection, cancellationToken);
            await CreateV14Async(connection, cancellationToken);
            return;
        }
        if (version == 1)
        {
            throw new LegacyDevelopmentSchemaException();
        }
        if (version < 2 || version > CurrentVersion)
        {
            throw new NotSupportedException(
                $"不支持 AgenTally SQLite Schema 版本 {version}。当前版本是 {CurrentVersion}。");
        }

        await ConfigureConnectionAsync(connection, cancellationToken);
        if (version >= 4)
        {
            await EnsureV4PricingExtensionsAsync(connection, cancellationToken);
        }
        if (version <= 2)
        {
            await MigrateV2ToV3Async(connection, cancellationToken);
        }
        if (version <= 3)
        {
            await MigrateV3ToV4Async(connection, cancellationToken);
        }
        if (version <= 4)
        {
            await MigrateV4ToV5Async(connection, cancellationToken);
        }
        if (version <= 5)
        {
            await MigrateV5ToV6Async(connection, cancellationToken);
        }
        if (version <= 6)
        {
            await MigrateV6ToV7Async(connection, cancellationToken);
        }
        if (version <= 7)
        {
            await MigrateV7ToV8Async(connection, cancellationToken);
        }
        if (version <= 8)
        {
            await MigrateV8ToV9Async(connection, cancellationToken);
        }
        if (version <= 9)
        {
            await MigrateV9ToV10Async(connection, cancellationToken);
        }
        if (version <= 10)
        {
            await MigrateV10ToV11Async(connection, cancellationToken);
        }
        if (version <= 11)
        {
            await MigrateV11ToV12Async(connection, cancellationToken);
        }
        if (version <= 12)
        {
            await MigrateV12ToV13Async(connection, cancellationToken);
        }
        if (version <= 13)
        {
            await MigrateV13ToV14Async(connection, cancellationToken);
        }

        await ApplyModelIdentityCatalogUpgradeAsync(
            connection,
            cancellationToken);
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 2000;
            PRAGMA foreign_keys = ON;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateV14Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CreateV14;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$model_catalog_version",
            ModelIdentityCanonicalizer.CatalogVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV2ToV3Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV2ToV3;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV3ToV4Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV3ToV4;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV4ToV5Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV4ToV5;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV5ToV6Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV5ToV6;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV6ToV7Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV6ToV7;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV7ToV8Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV7ToV8;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV8ToV9Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV8ToV9;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV9ToV10Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV9ToV10;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV10ToV11Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV10ToV11;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV11ToV12Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeStoredModelsAsync(
            connection,
            transaction,
            cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV11ToV12;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV12ToV13Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeStoredModelsAsync(
            connection,
            transaction,
            cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV12ToV13;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MigrateV13ToV14Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await NormalizeStoredModelsAsync(
            connection,
            transaction,
            cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrateV13ToV14;
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$model_catalog_version",
            ModelIdentityCanonicalizer.CatalogVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ApplyModelIdentityCatalogUpgradeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT catalog_version
                FROM model_identity_catalog_state
                WHERE singleton_id = 1;
                """;
            string? appliedVersion =
                await current.ExecuteScalarAsync(cancellationToken) as string;
            if (string.Equals(
                    appliedVersion,
                    ModelIdentityCanonicalizer.CatalogVersion,
                    StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await NormalizeStoredModelsAsync(
            connection,
            transaction,
            cancellationToken);
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            INSERT INTO model_identity_catalog_state (
                singleton_id,
                catalog_version,
                applied_at_unix_ms
            ) VALUES (
                1,
                $catalog_version,
                $applied_at_unix_ms
            )
            ON CONFLICT(singleton_id) DO UPDATE SET
                catalog_version = excluded.catalog_version,
                applied_at_unix_ms = excluded.applied_at_unix_ms;

            DELETE FROM pricing_catalog_state;
            """;
        update.Parameters.AddWithValue(
            "$catalog_version",
            ModelIdentityCanonicalizer.CatalogVersion);
        update.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task NormalizeStoredModelsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var canonicalModelsByStoredModel =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var events = new List<StoredModelIdentity>();
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT rowid, agent_id, normalized_model, provider_id
                FROM usage_events
                WHERE normalized_model IS NOT NULL
                  AND TRIM(normalized_model) <> '';
                """;
            await using SqliteDataReader reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new StoredModelIdentity(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        await using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE usage_events
                SET normalized_model = $normalized_model
                WHERE rowid = $rowid;
                """;
            foreach (StoredModelIdentity value in events)
            {
                string canonical = ModelIdentityCanonicalizer.Canonicalize(
                    value.Model,
                    value.AgentId,
                    value.ProviderId)!;
                if (string.Equals(
                        canonical,
                        value.Model,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!canonicalModelsByStoredModel.TryGetValue(
                        value.Model,
                        out HashSet<string>? canonicalModels))
                {
                    canonicalModels = new HashSet<string>(StringComparer.Ordinal);
                    canonicalModelsByStoredModel.Add(
                        value.Model,
                        canonicalModels);
                }
                canonicalModels.Add(canonical);

                update.Parameters.Clear();
                update.Parameters.AddWithValue("$normalized_model", canonical);
                update.Parameters.AddWithValue("$rowid", value.RowId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var overrides = new List<string>();
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT normalized_model
                FROM pricing_overrides
                ORDER BY updated_at_unix_ms DESC, normalized_model;
                """;
            await using SqliteDataReader reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                overrides.Add(reader.GetString(0));
            }
        }

        await using SqliteCommand moveOverride = connection.CreateCommand();
        moveOverride.Transaction = transaction;
        moveOverride.CommandText = """
            UPDATE OR IGNORE pricing_overrides
            SET normalized_model = $canonical_model
            WHERE normalized_model = $stored_model;

            DELETE FROM pricing_overrides
            WHERE normalized_model = $stored_model;
            """;
        foreach (string storedModel in overrides)
        {
            string canonical =
                ModelIdentityCanonicalizer.Canonicalize(storedModel)!;
            if (string.Equals(
                    canonical,
                    storedModel,
                    StringComparison.Ordinal) &&
                canonicalModelsByStoredModel.TryGetValue(
                    storedModel,
                    out HashSet<string>? eventCanonicalModels) &&
                eventCanonicalModels.Count == 1)
            {
                canonical = eventCanonicalModels.Single();
            }
            if (string.Equals(
                    canonical,
                    storedModel,
                    StringComparison.Ordinal))
            {
                continue;
            }

            moveOverride.Parameters.Clear();
            moveOverride.Parameters.AddWithValue("$canonical_model", canonical);
            moveOverride.Parameters.AddWithValue("$stored_model", storedModel);
            await moveOverride.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record StoredModelIdentity(
        long RowId,
        string AgentId,
        string Model,
        string? ProviderId);

    private static async Task EnsureV4PricingExtensionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        bool hasOutputMultiplier = await HasColumnAsync(
            connection,
            "usage_events",
            "output_price_context_multiplier",
            cancellationToken);
        bool hasMissingCategories = await HasColumnAsync(
            connection,
            "usage_events",
            "pricing_missing_categories",
            cancellationToken);
        bool hasAcceptedParserVersion = await HasColumnAsync(
            connection,
            "source_instances",
            "accepted_parser_version",
            cancellationToken);
        if (hasOutputMultiplier &&
            hasMissingCategories &&
            hasAcceptedParserVersion)
        {
            await EnsurePricingTablesAsync(connection, cancellationToken);
            await EnsureStagingMergeIndexesAsync(connection, cancellationToken);
            return;
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(
            Environment.NewLine,
            hasOutputMultiplier
                ? string.Empty
                : """
                  ALTER TABLE usage_events
                  ADD COLUMN output_price_context_multiplier TEXT NULL;
                  """,
            hasMissingCategories
                ? string.Empty
                : """
                  ALTER TABLE usage_events
                  ADD COLUMN pricing_missing_categories INTEGER NOT NULL DEFAULT 0;
                  """,
            hasAcceptedParserVersion
                ? string.Empty
                : """
                  ALTER TABLE source_instances
                  ADD COLUMN accepted_parser_version TEXT NULL;
                  """,
            PricingTables,
            StagingMergeIndexes);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private const string PricingTables = """
        CREATE TABLE IF NOT EXISTS pricing_overrides (
            normalized_model TEXT NOT NULL PRIMARY KEY,
            input_rate_usd_per_million TEXT NOT NULL,
            cached_input_rate_usd_per_million TEXT NULL,
            cache_write_rate_usd_per_million TEXT NULL,
            output_rate_usd_per_million TEXT NOT NULL,
            long_context_threshold_tokens INTEGER NULL,
            long_context_input_multiplier TEXT NOT NULL,
            long_context_output_multiplier TEXT NOT NULL,
            updated_at_unix_ms INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS pricing_catalog_state (
            singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
            catalog_version TEXT NOT NULL,
            applied_at_unix_ms INTEGER NOT NULL
        );
        """;

    private const string StagingMergeIndexes = """
        CREATE INDEX IF NOT EXISTS ix_usage_events_source_event
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                event_id);

        CREATE INDEX IF NOT EXISTS ix_usage_events_source_revision
            ON usage_events (
                agent_id,
                source_instance_id,
                source_entity_id,
                source_revision);
        """;

    private static async Task EnsurePricingTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = PricingTables;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureStagingMergeIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = StagingMergeIndexes;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    column,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
