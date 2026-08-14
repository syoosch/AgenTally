using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Queries;

public sealed class SqliteUsageQueryService : IUsageQueryService
{
    private const string UsagePredicate = """
        usage_events.occurred_at_unix_ms >= $start
        AND usage_events.occurred_at_unix_ms < $end
        AND ($agent_id IS NULL OR usage_events.agent_id = $agent_id)
        AND (
            $normalized_model IS NULL
            OR COALESCE(usage_events.normalized_model, '未知模型') =
                $normalized_model
        )
        AND (
            (
                $unidentified_project_only = 1
                AND usage_events.project_id IS NULL
            )
            OR (
                $unidentified_project_only = 0
                AND (
                    $project_id IS NULL
                    OR usage_events.project_id = $project_id
                )
            )
        )
        AND (
            $root_session_id IS NULL
            OR EXISTS (
                WITH RECURSIVE descendants(
                    agent_id,
                    source_instance_id,
                    session_id
                ) AS (
                    SELECT
                        agent_id,
                        source_instance_id,
                        session_id
                    FROM usage_sessions
                    WHERE session_id = $root_session_id
                      AND (
                          $root_agent_id IS NULL
                          OR agent_id = $root_agent_id
                      )
                      AND (
                          $root_source_instance_id IS NULL
                          OR source_instance_id = $root_source_instance_id
                      )

                    UNION

                    SELECT DISTINCT
                        agent_id,
                        source_instance_id,
                        session_id
                    FROM usage_events AS root_events
                    WHERE root_events.session_id = $root_session_id
                      AND (
                          $root_agent_id IS NULL
                          OR root_events.agent_id = $root_agent_id
                      )
                      AND (
                          $root_source_instance_id IS NULL
                          OR root_events.source_instance_id =
                              $root_source_instance_id
                      )

                    UNION

                    SELECT
                        child.agent_id,
                        child.source_instance_id,
                        child.session_id
                    FROM usage_sessions AS child
                    INNER JOIN descendants AS parent
                        ON parent.agent_id = child.agent_id
                       AND parent.source_instance_id = child.source_instance_id
                       AND parent.session_id = child.direct_parent_session_id
                    WHERE child.relation_state = 1
                )
                SELECT 1
                FROM descendants
                WHERE descendants.agent_id = usage_events.agent_id
                  AND descendants.source_instance_id =
                      usage_events.source_instance_id
                  AND descendants.session_id = usage_events.session_id
            )
        )
        """;

    private const string MetricSelect = """
        SUM({0}_value),
        COALESCE(SUM(CASE WHEN {0}_value IS NOT NULL THEN 1 ELSE 0 END), 0),
        COALESCE(SUM(CASE
            WHEN {0}_value IS NULL AND {0}_origin = 0 THEN 1 ELSE 0 END), 0),
        COALESCE(SUM(CASE
            WHEN {0}_value IS NULL AND {0}_origin = 6 THEN 1 ELSE 0 END), 0)
        """;

    private const string PricingSelect = """
        SUM(CASE
            WHEN {0}.pricing_status IN (1, 2)
            THEN CAST({0}.estimated_cost_usd AS REAL)
        END),
        COALESCE(SUM(CASE WHEN {0}.pricing_status = 1 THEN 1 ELSE 0 END), 0),
        COALESCE(SUM(CASE WHEN {0}.pricing_status = 2 THEN 1 ELSE 0 END), 0),
        COALESCE(SUM(CASE WHEN {0}.pricing_status = 0 THEN 1 ELSE 0 END), 0),
        (
            COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 1) <> 0
                THEN 1 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 2) <> 0
                THEN 2 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 4) <> 0
                THEN 4 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 8) <> 0
                THEN 8 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 16) <> 0
                THEN 16 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 32) <> 0
                THEN 32 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 64) <> 0
                THEN 64 ELSE 0 END), 0)
            + COALESCE(MAX(CASE WHEN ({0}.pricing_missing_categories & 128) <> 0
                THEN 128 ELSE 0 END), 0)
        )
        """;

    private const string SessionTreeCte = """
        WITH RECURSIVE
        all_sessions (
            agent_id,
            source_instance_id,
            session_id,
            session_kind,
            session_role,
            direct_parent_session_id,
            relation_state,
            project_id,
            project_path,
            session_name
        ) AS (
            SELECT
                agent_id,
                source_instance_id,
                session_id,
                session_kind,
                session_role,
                direct_parent_session_id,
                relation_state,
                project_id,
                project_path,
                session_name
            FROM usage_sessions

            UNION ALL

            SELECT DISTINCT
                events.agent_id,
                events.source_instance_id,
                events.session_id,
                0,
                0,
                NULL,
                0,
                events.project_id,
                events.project_path,
                NULL
            FROM usage_events AS events
            WHERE events.session_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM usage_sessions AS known
                  WHERE known.agent_id = events.agent_id
                    AND known.source_instance_id = events.source_instance_id
                    AND known.session_id = events.session_id
              )
        ),
        session_tree (
            agent_id,
            source_instance_id,
            root_session_id,
            session_id,
            session_kind,
            session_role,
            direct_parent_session_id,
            project_id,
            project_path,
            session_name,
            depth
        ) AS (
            SELECT
                session.agent_id,
                session.source_instance_id,
                session.session_id,
                session.session_id,
                session.session_kind,
                session.session_role,
                session.direct_parent_session_id,
                session.project_id,
                session.project_path,
                session.session_name,
                0
            FROM all_sessions AS session
            WHERE NOT EXISTS (
                SELECT 1
                FROM all_sessions AS parent
                WHERE session.relation_state = 1
                  AND parent.agent_id = session.agent_id
                  AND parent.source_instance_id = session.source_instance_id
                  AND parent.session_id = session.direct_parent_session_id
            )

            UNION ALL

            SELECT
                child.agent_id,
                child.source_instance_id,
                parent.root_session_id,
                child.session_id,
                child.session_kind,
                child.session_role,
                child.direct_parent_session_id,
                child.project_id,
                child.project_path,
                child.session_name,
                parent.depth + 1
            FROM session_tree AS parent
            INNER JOIN all_sessions AS child
                ON child.agent_id = parent.agent_id
               AND child.source_instance_id = parent.source_instance_id
               AND child.direct_parent_session_id = parent.session_id
            WHERE child.relation_state = 1
              AND parent.depth < 1024
        )
        """;

    private readonly SqliteConnectionFactory _connections;
    private readonly OfflinePriceCatalog _catalog;

    public SqliteUsageQueryService(
        SqliteConnectionFactory connections,
        OfflinePriceCatalog? catalog = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _catalog = catalog ?? OfflinePriceCatalog.Default;
    }

    public async Task<UsageOverview> GetOverviewAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                {Metric("normalized_total")},
                {Metric("uncached_input")},
                {Metric("output")},
                {Metric("cache_read")},
                {Metric("cache_write")},
                {Metric("input_reported")},
                {Metric("reasoning")},
                {Metric("tool")},
                {Metric("reported_total")},
                {Pricing("usage_events")},
                MIN(occurred_at_unix_ms),
                MAX(occurred_at_unix_ms)
            FROM usage_events
            WHERE {UsagePredicate};
            """;
        BindFilter(command, filter);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("SQLite 未返回概览聚合行。");
        }

        MetricAggregate normalizedTotal = ReadAggregate(reader, 1);
        MetricAggregate uncachedInput = ReadAggregate(reader, 5);
        MetricAggregate output = ReadAggregate(reader, 9);
        MetricAggregate cacheRead = ReadAggregate(reader, 13);
        MetricAggregate cacheWrite = ReadAggregate(reader, 17);
        var metrics = new UsageMetricSet(
            ReadAggregate(reader, 21),
            uncachedInput,
            cacheRead,
            cacheWrite,
            output,
            ReadAggregate(reader, 25),
            ReadAggregate(reader, 29),
            ReadAggregate(reader, 33),
            normalizedTotal);
        return new UsageOverview(
            reader.GetInt64(0),
            normalizedTotal,
            uncachedInput,
            output,
            cacheRead,
            cacheWrite,
            ReadNullableTimestamp(reader, 43))
        {
            FirstOccurredAtUtc = ReadNullableTimestamp(reader, 42),
            Metrics = metrics,
            Pricing = ReadPricingAggregate(reader, 37)
        };
    }

    public async Task<IReadOnlyList<UsageTrendPoint>> GetTrendAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                (occurred_at_unix_ms / 3600000) * 3600000 AS bucket_unix_ms,
                COUNT(*),
                {Metric("normalized_total")},
                {Metric("uncached_input")},
                {Metric("output")},
                {Metric("cache_read")},
                {Metric("cache_write")},
                {Metric("input_reported")},
                {Metric("reasoning")},
                {Metric("tool")},
                {Metric("reported_total")},
                {Pricing("usage_events")}
            FROM usage_events
            WHERE {UsagePredicate}
            GROUP BY bucket_unix_ms
            ORDER BY bucket_unix_ms ASC;
            """;
        BindFilter(command, filter);

        var points = new List<UsageTrendPoint>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            MetricAggregate normalizedTotal = ReadAggregate(reader, 2);
            MetricAggregate uncachedInput = ReadAggregate(reader, 6);
            MetricAggregate output = ReadAggregate(reader, 10);
            MetricAggregate cacheRead = ReadAggregate(reader, 14);
            MetricAggregate cacheWrite = ReadAggregate(reader, 18);
            points.Add(new UsageTrendPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                normalizedTotal,
                uncachedInput,
                output,
                cacheRead,
                cacheWrite,
                reader.GetInt64(1))
            {
                Metrics = new UsageMetricSet(
                    ReadAggregate(reader, 22),
                    uncachedInput,
                    cacheRead,
                    cacheWrite,
                    output,
                    ReadAggregate(reader, 26),
                    ReadAggregate(reader, 30),
                    ReadAggregate(reader, 34),
                    normalizedTotal),
                Pricing = ReadPricingAggregate(reader, 38)
            });
        }

        return points;
    }

    public async Task<IReadOnlyList<UsageRecordRow>> GetRecentRecordsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                source_instance_id,
                source_entity_id,
                event_id,
                occurred_at_unix_ms,
                agent_id,
                COALESCE(normalized_model, '未知模型'),
                normalized_total_value,
                uncached_input_value,
                output_value,
                cache_read_value,
                cache_write_value,
                completion_state,
                data_quality,
                pricing_status,
                estimated_cost_usd,
                pricing_missing_categories,
                price_catalog_version,
                price_rule_id,
                input_rate_usd_per_million,
                cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million,
                output_rate_usd_per_million,
                price_context_multiplier,
                output_price_context_multiplier
            FROM usage_events
            WHERE {UsagePredicate}
            ORDER BY occurred_at_unix_ms DESC,
                     agent_id ASC,
                     source_instance_id ASC,
                     dedup_key ASC
            LIMIT $limit OFFSET $offset;
            """;
        BindFilter(command, filter);
        command.Parameters.AddWithValue("$limit", filter.Limit);
        command.Parameters.AddWithValue("$offset", filter.Offset);

        var records = new List<UsageRecordRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new UsageRecordRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.GetString(4),
                reader.GetString(5),
                ReadNullableInt64(reader, 6),
                ReadNullableInt64(reader, 7),
                ReadNullableInt64(reader, 8),
                ReadNullableInt64(reader, 9),
                ReadNullableInt64(reader, 10),
                (CompletionState)reader.GetInt32(11),
                (DataQuality)reader.GetInt32(12))
            {
                Pricing = ReadEventPrice(reader, 13)
            });
        }

        return records;
    }

    public async Task<IReadOnlyList<ModelUsageRow>> GetModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                COALESCE(normalized_model, '未知模型') AS model,
                COUNT(*),
                {Metric("normalized_total")},
                {Metric("uncached_input")},
                {Metric("output")},
                {Metric("cache_read")},
                {Metric("cache_write")},
                {Metric("input_reported")},
                {Metric("reasoning")},
                {Metric("tool")},
                {Metric("reported_total")},
                {Pricing("usage_events")},
                MIN(occurred_at_unix_ms),
                MAX(occurred_at_unix_ms)
            FROM usage_events
            WHERE {UsagePredicate}
            GROUP BY model
            ORDER BY SUM(normalized_total_value) DESC,
                     model COLLATE NOCASE ASC;
            """;
        BindFilter(command, filter);

        var models = new List<ModelUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            MetricAggregate normalizedTotal = ReadAggregate(reader, 2);
            MetricAggregate uncachedInput = ReadAggregate(reader, 6);
            MetricAggregate output = ReadAggregate(reader, 10);
            MetricAggregate cacheRead = ReadAggregate(reader, 14);
            MetricAggregate cacheWrite = ReadAggregate(reader, 18);
            models.Add(new ModelUsageRow(
                reader.GetString(0),
                reader.GetInt64(1),
                normalizedTotal,
                uncachedInput,
                output,
                cacheRead,
                cacheWrite)
            {
                Metrics = new UsageMetricSet(
                    ReadAggregate(reader, 22),
                    uncachedInput,
                    cacheRead,
                    cacheWrite,
                    output,
                    ReadAggregate(reader, 26),
                    ReadAggregate(reader, 30),
                    ReadAggregate(reader, 34),
                    normalizedTotal),
                Pricing = ReadPricingAggregate(reader, 38),
                StartedAtUtc = ReadNullableTimestamp(reader, 43),
                LastActivityUtc = ReadNullableTimestamp(reader, 44)
            });
        }

        return models;
    }

    public async Task<IReadOnlyList<AgentUsageRow>> GetAgentsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                agent_id,
                COUNT(*),
                {Metric("normalized_total")},
                {Metric("uncached_input")},
                {Metric("output")},
                {Metric("cache_read")},
                {Metric("cache_write")},
                {Metric("input_reported")},
                {Metric("reasoning")},
                {Metric("tool")},
                {Metric("reported_total")},
                {Pricing("usage_events")},
                MIN(occurred_at_unix_ms),
                MAX(occurred_at_unix_ms)
            FROM usage_events
            WHERE {UsagePredicate}
            GROUP BY agent_id
            ORDER BY SUM(normalized_total_value) DESC,
                     agent_id COLLATE NOCASE ASC;
            """;
        BindFilter(command, filter);

        var agents = new List<AgentUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            MetricAggregate normalizedTotal = ReadAggregate(reader, 2);
            MetricAggregate uncachedInput = ReadAggregate(reader, 6);
            MetricAggregate output = ReadAggregate(reader, 10);
            MetricAggregate cacheRead = ReadAggregate(reader, 14);
            MetricAggregate cacheWrite = ReadAggregate(reader, 18);
            agents.Add(new AgentUsageRow(
                reader.GetString(0),
                reader.GetInt64(1),
                normalizedTotal,
                uncachedInput,
                output,
                cacheRead,
                cacheWrite)
            {
                Metrics = new UsageMetricSet(
                    ReadAggregate(reader, 22),
                    uncachedInput,
                    cacheRead,
                    cacheWrite,
                    output,
                    ReadAggregate(reader, 26),
                    ReadAggregate(reader, 30),
                    ReadAggregate(reader, 34),
                    normalizedTotal),
                Pricing = ReadPricingAggregate(reader, 38),
                StartedAtUtc = ReadNullableTimestamp(reader, 43),
                LastActivityUtc = ReadNullableTimestamp(reader, 44)
            });
        }

        return agents;
    }

    public async Task<IReadOnlyList<AgentModelUsageRow>> GetAgentModelsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                agent_id,
                COALESCE(normalized_model, '未知模型') AS model,
                COUNT(*),
                {Metric("normalized_total")},
                {Metric("uncached_input")},
                {Metric("output")},
                {Metric("cache_read")},
                {Metric("cache_write")},
                {Metric("input_reported")},
                {Metric("reasoning")},
                {Metric("tool")},
                {Metric("reported_total")},
                {Pricing("usage_events")},
                MIN(occurred_at_unix_ms),
                MAX(occurred_at_unix_ms)
            FROM usage_events
            WHERE {UsagePredicate}
            GROUP BY agent_id, model
            ORDER BY SUM(normalized_total_value) DESC,
                     agent_id COLLATE NOCASE ASC,
                     model COLLATE NOCASE ASC;
            """;
        BindFilter(command, filter);

        var rows = new List<AgentModelUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            MetricAggregate normalizedTotal = ReadAggregate(reader, 3);
            MetricAggregate uncachedInput = ReadAggregate(reader, 7);
            MetricAggregate output = ReadAggregate(reader, 11);
            MetricAggregate cacheRead = ReadAggregate(reader, 15);
            MetricAggregate cacheWrite = ReadAggregate(reader, 19);
            rows.Add(new AgentModelUsageRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                normalizedTotal,
                uncachedInput,
                output,
                cacheRead,
                cacheWrite)
            {
                Metrics = new UsageMetricSet(
                    ReadAggregate(reader, 23),
                    uncachedInput,
                    cacheRead,
                    cacheWrite,
                    output,
                    ReadAggregate(reader, 27),
                    ReadAggregate(reader, 31),
                    ReadAggregate(reader, 35),
                    normalizedTotal),
                Pricing = ReadPricingAggregate(reader, 39),
                StartedAtUtc = ReadNullableTimestamp(reader, 44),
                LastActivityUtc = ReadNullableTimestamp(reader, 45)
            });
        }

        return rows;
    }

    public async Task<UsageFilterValues> GetFilterValuesAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);

        UsageFilter agentFilter =
            CreateFacetFilter(filter, FilterFacet.Agent);
        IReadOnlyList<string> agentIds = await ReadStringsAsync(
            connection,
            $"""
            SELECT DISTINCT agent_id
            FROM usage_events
            WHERE {CreateFacetPredicate(agentFilter)}
            ORDER BY agent_id COLLATE NOCASE ASC;
            """,
            agentFilter,
            cancellationToken);
        UsageFilter modelFilter =
            CreateFacetFilter(filter, FilterFacet.Model);
        IReadOnlyList<string> models = await ReadStringsAsync(
            connection,
            $"""
            SELECT DISTINCT COALESCE(normalized_model, '未知模型') AS model
            FROM usage_events
            WHERE {CreateFacetPredicate(modelFilter)}
            ORDER BY model COLLATE NOCASE ASC;
            """,
            modelFilter,
            cancellationToken);
        UsageFilter projectFilter =
            CreateFacetFilter(filter, FilterFacet.Project);
        var projects = new List<ProjectFilterValue>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"""
                WITH candidate_projects AS (
                    SELECT DISTINCT usage_events.project_id
                    FROM usage_events
                    WHERE {CreateFacetPredicate(projectFilter)}
                      AND usage_events.project_id IS NOT NULL
                )
                SELECT
                    candidate_projects.project_id,
                    COALESCE(
                        MAX(CASE
                            WHEN INSTR(
                                LOWER(REPLACE(
                                    project_events.project_path,
                                    '/',
                                    '\')),
                                '\.codex\worktrees\') = 0
                            THEN project_events.project_path
                        END),
                        MAX(project_events.project_path)
                    ) AS project_path
                FROM candidate_projects
                INNER JOIN usage_events AS project_events
                    ON project_events.project_id = candidate_projects.project_id
                GROUP BY candidate_projects.project_id
                ORDER BY
                    CASE WHEN project_path IS NULL THEN 1 ELSE 0 END,
                    project_path COLLATE NOCASE ASC,
                    candidate_projects.project_id ASC;
                """;
            BindFilter(command, projectFilter);
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? path = reader.IsDBNull(1) ? null : reader.GetString(1);
                projects.Add(new ProjectFilterValue(
                    reader.GetString(0),
                    path,
                    path is null
                        ? PathAvailability.Unavailable
                        : PathAvailability.Available));
            }
        }

        return new UsageFilterValues(agentIds, models)
        {
            Projects = projects
        };
    }

    private static UsageFilter CreateFacetFilter(
        UsageFilter filter,
        FilterFacet ignoredFacet) => new(
            filter.StartInclusiveUtc,
            filter.EndExclusiveUtc,
            ignoredFacet == FilterFacet.Agent ? null : filter.AgentId,
            ignoredFacet == FilterFacet.Model ? null : filter.NormalizedModel,
            filter.Limit,
            filter.Offset,
            ignoredFacet == FilterFacet.Project ? null : filter.ProjectId,
            filter.RootSessionId,
            ignoredFacet == FilterFacet.Project
                ? false
                : filter.UnidentifiedProjectOnly,
            filter.RootIdentity);

    private static string CreateFacetPredicate(UsageFilter filter)
    {
        if (filter.RootSessionId is not null)
        {
            return UsagePredicate;
        }

        var predicates = new List<string>
        {
            "usage_events.occurred_at_unix_ms >= $start",
            "usage_events.occurred_at_unix_ms < $end"
        };
        if (filter.AgentId is not null)
        {
            predicates.Add("usage_events.agent_id = $agent_id");
        }

        if (filter.NormalizedModel is not null)
        {
            predicates.Add(
                "COALESCE(usage_events.normalized_model, '未知模型') = " +
                "$normalized_model");
        }

        if (filter.UnidentifiedProjectOnly)
        {
            predicates.Add("usage_events.project_id IS NULL");
        }
        else if (filter.ProjectId is not null)
        {
            predicates.Add("usage_events.project_id = $project_id");
        }

        return string.Join(
            Environment.NewLine + "AND ",
            predicates);
    }

    public async Task<IReadOnlyList<PriceSettingRow>> GetPriceSettingsAsync(
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, PriceSettingAccumulator>(
            StringComparer.Ordinal);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using (SqliteCommand overrides = connection.CreateCommand())
        {
            overrides.CommandText = """
                SELECT
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier
                FROM pricing_overrides
                ORDER BY normalized_model;
                """;
            await using SqliteDataReader reader =
                await overrides.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string model = reader.GetString(0);
                PriceSettingAccumulator row = GetOrCreate(rows, model);
                row.CustomRate = new ModelPriceRate(
                    model,
                    ReadRequiredDecimal(reader, 1),
                    ReadNullableDecimal(reader, 2),
                    ReadNullableDecimal(reader, 3),
                    ReadRequiredDecimal(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    ReadRequiredDecimal(reader, 6),
                    ReadRequiredDecimal(reader, 7));
            }
        }

        await using (SqliteCommand observed = connection.CreateCommand())
        {
            observed.CommandText = """
                SELECT normalized_model, COUNT(*)
                FROM usage_events
                WHERE normalized_model IS NOT NULL
                  AND TRIM(normalized_model) <> ''
                GROUP BY normalized_model
                ORDER BY normalized_model COLLATE NOCASE;
                """;
            await using SqliteDataReader reader =
                await observed.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string model = reader.GetString(0);
                PriceSettingAccumulator row = GetOrCreate(rows, model);
                row.ObservedRecords += reader.GetInt64(1);
            }
        }

        foreach ((string model, PriceSettingAccumulator row) in rows)
        {
            if (_catalog.TryResolve(model, out ResolvedPriceRule? rule) &&
                rule is not null)
            {
                row.BuiltInRate = rule.Rate;
            }
        }

        return rows
            .Select(static pair => new PriceSettingRow(
                pair.Key,
                pair.Value.BuiltInRate,
                pair.Value.CustomRate,
                pair.Value.ObservedRecords))
            .OrderBy(static row => row.NormalizedModel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RootSessionPage> GetRootSessionsAsync(
        RootSessionPageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte},
            root_metadata AS (
                SELECT
                    tree.agent_id,
                    tree.source_instance_id,
                    tree.root_session_id,
                    tree.session_name,
                    tree.project_id,
                    tree.project_path
                FROM session_tree AS tree
                WHERE tree.session_id = tree.root_session_id
            ),
            aggregated AS (
                SELECT
                    tree.agent_id,
                    tree.source_instance_id,
                    tree.root_session_id,
                    root.session_name,
                    MIN(usage_events.occurred_at_unix_ms)
                        AS started_at_unix_ms,
                    MAX(usage_events.occurred_at_unix_ms)
                        AS last_activity_unix_ms,
                    root.project_id,
                    root.project_path,
                    COUNT(*),
                    COUNT(DISTINCT CASE
                        WHEN tree.session_id <> tree.root_session_id
                         AND tree.session_kind = 2
                        THEN tree.session_id
                    END),
                    {AllMetrics("usage_events")}
                    ,{Pricing("usage_events")}
                FROM usage_events
                INNER JOIN session_tree AS tree
                    ON tree.agent_id = usage_events.agent_id
                   AND tree.source_instance_id = usage_events.source_instance_id
                   AND tree.session_id = usage_events.session_id
                INNER JOIN root_metadata AS root
                    ON root.agent_id = tree.agent_id
                   AND root.source_instance_id = tree.source_instance_id
                   AND root.root_session_id = tree.root_session_id
                WHERE {UsagePredicate}
                GROUP BY
                    tree.agent_id,
                    tree.source_instance_id,
                    tree.root_session_id,
                    root.session_name,
                    root.project_id,
                    root.project_path
            )
            SELECT *
            FROM aggregated
            WHERE $after_activity IS NULL
               OR last_activity_unix_ms < $after_activity
               OR (
                   last_activity_unix_ms = $after_activity
                   AND (
                       agent_id > $after_agent_id
                       OR (
                           agent_id = $after_agent_id
                           AND source_instance_id > $after_source_instance_id
                       )
                       OR (
                           agent_id = $after_agent_id
                           AND source_instance_id = $after_source_instance_id
                           AND root_session_id > $after_session_id
                       )
                   )
               )
            ORDER BY last_activity_unix_ms DESC,
                     agent_id ASC,
                     source_instance_id ASC,
                     root_session_id ASC
            LIMIT $page_size_plus_one;
            """;
        BindFilter(command, request.Filter);
        command.Parameters.AddWithValue(
            "$after_activity",
            request.After is null
                ? DBNull.Value
                : request.After.LastActivityUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$after_agent_id",
            (object?)request.After?.Identity.AgentId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$after_source_instance_id",
            (object?)request.After?.Identity.SourceInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$after_session_id",
            (object?)request.After?.Identity.RootSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$page_size_plus_one",
            checked(request.PageSize + 1));

        var rows = new List<RootSessionSummaryRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string? projectPath = reader.IsDBNull(7) ? null : reader.GetString(7);
            rows.Add(new RootSessionSummaryRow(
                new RootSessionIdentity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                projectPath,
                projectPath is null
                    ? PathAvailability.Unavailable
                    : PathAvailability.Available,
                reader.GetInt64(8),
                reader.GetInt32(9),
                ReadMetricSet(reader, 10))
            {
                SessionName = reader.IsDBNull(3) ? null : reader.GetString(3),
                Pricing = ReadPricingAggregate(reader, 46)
            });
        }

        bool hasMore = rows.Count > request.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        RootSessionCursor? next = hasMore
            ? new RootSessionCursor(
                rows[^1].LastActivityUtc,
                rows[^1].Identity)
            : null;
        return new RootSessionPage(rows, next);
    }

    public async Task<RootSessionDetail?> GetRootSessionDetailAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(identity);
        UsageFilter scopedFilter = WithRootSession(filter, identity);
        RootSessionPage summaryPage = await GetRootSessionsAsync(
            new RootSessionPageRequest(scopedFilter, pageSize: 1),
            cancellationToken);
        RootSessionSummaryRow? summary = summaryPage.Items
            .SingleOrDefault(row => row.Identity == identity);
        if (summary is null)
        {
            return null;
        }

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        Dictionary<string, List<SessionModelUsageRow>> models =
            await ReadSessionModelsAsync(
                connection,
                scopedFilter,
                identity.RootSessionId,
                cancellationToken);
        IReadOnlyList<SessionModelUsageRow> rootModels =
            await ReadRootModelsAsync(
                connection,
                scopedFilter,
                identity.RootSessionId,
                cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte}
            SELECT
                tree.session_id,
                tree.direct_parent_session_id,
                tree.session_kind,
                tree.session_role,
                tree.depth,
                COUNT(usage_events.dedup_key),
                {AllMetrics("usage_events")},
                {Pricing("usage_events")}
            FROM session_tree AS tree
            LEFT JOIN usage_events
                ON usage_events.agent_id = tree.agent_id
               AND usage_events.source_instance_id = tree.source_instance_id
               AND usage_events.session_id = tree.session_id
               AND {UsagePredicate}
            WHERE tree.root_session_id = $detail_root_session_id
            GROUP BY
                tree.agent_id,
                tree.source_instance_id,
                tree.session_id,
                tree.direct_parent_session_id,
                tree.session_kind,
                tree.session_role,
                tree.depth
            ORDER BY tree.depth ASC, tree.session_id ASC;
            """;
        BindFilter(command, scopedFilter);
        command.Parameters.AddWithValue(
            "$detail_root_session_id",
            identity.RootSessionId);

        var contributions = new List<SessionContributionRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string sessionId = reader.GetString(0);
            contributions.Add(new SessionContributionRow(
                sessionId,
                reader.IsDBNull(1) ? null : reader.GetString(1),
                (SessionKind)reader.GetInt32(2),
                reader.GetInt32(4),
                reader.GetInt64(5),
                ReadMetricSet(reader, 6),
                models.TryGetValue(sessionId, out List<SessionModelUsageRow>? value)
                    ? value
                    : [])
            {
                Pricing = ReadPricingAggregate(reader, 42),
                SessionRole = (SessionRole)reader.GetInt32(3)
            });
        }

        return new RootSessionDetail(summary, contributions)
        {
            Models = rootModels
        };
    }

    public async Task<IReadOnlyList<ProjectUsageRow>> GetProjectsAsync(
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte}
            SELECT
                usage_events.project_id,
                COALESCE(
                    MAX(CASE
                        WHEN INSTR(
                            LOWER(REPLACE(usage_events.project_path, '/', '\')),
                            '\.codex\worktrees\') = 0
                        THEN usage_events.project_path
                    END),
                    MAX(usage_events.project_path)
                ),
                MIN(usage_events.occurred_at_unix_ms),
                MAX(usage_events.occurred_at_unix_ms),
                COUNT(*),
                COUNT(DISTINCT
                    tree.agent_id || CHAR(31) ||
                    tree.source_instance_id || CHAR(31) ||
                    tree.root_session_id),
                {AllMetrics("usage_events")},
                {Pricing("usage_events")}
            FROM usage_events
            LEFT JOIN session_tree AS tree
                ON tree.agent_id = usage_events.agent_id
               AND tree.source_instance_id = usage_events.source_instance_id
               AND tree.session_id = usage_events.session_id
            WHERE {UsagePredicate}
            GROUP BY usage_events.project_id
            ORDER BY MAX(usage_events.occurred_at_unix_ms) DESC,
                     usage_events.project_id ASC;
            """;
        BindFilter(command, filter);

        var rows = new List<ProjectUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bool isUnidentified = reader.IsDBNull(0);
            string? path = reader.IsDBNull(1) ? null : reader.GetString(1);
            rows.Add(new ProjectUsageRow(
                isUnidentified
                    ? ProjectUsageRow.UnidentifiedProjectId
                    : reader.GetString(0),
                path,
                path is null
                    ? PathAvailability.Unavailable
                    : PathAvailability.Available,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.GetInt64(4),
                reader.GetInt32(5),
                ReadMetricSet(reader, 6))
            {
                Pricing = ReadPricingAggregate(reader, 42),
                IsUnidentified = isUnidentified
            });
        }

        return rows;
    }

    public async Task<TurnUsagePage> GetTurnsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(identity);
        string rootSessionId = identity.RootSessionId;
        UsageFilter scopedFilter = WithRootSession(filter, identity);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte},
            attributed_events AS (
                SELECT
                    usage_events.*,
                    CASE
                        WHEN attribution.attribution_state = 0
                         AND attribution.origin_session_id =
                             $turn_root_session_id
                        THEN attribution.origin_turn_id_hash
                        WHEN usage_events.session_id = $turn_root_session_id
                        THEN usage_events.turn_id_hash
                        ELSE NULL
                    END AS prompt_turn_id_hash
                FROM usage_events
                INNER JOIN session_tree AS tree
                    ON tree.agent_id = usage_events.agent_id
                   AND tree.source_instance_id =
                       usage_events.source_instance_id
                   AND tree.session_id = usage_events.session_id
                LEFT JOIN usage_turn_attributions AS attribution
                    ON attribution.agent_id = usage_events.agent_id
                   AND attribution.source_instance_id =
                       usage_events.source_instance_id
                   AND attribution.session_id = usage_events.session_id
                   AND attribution.turn_id_hash = usage_events.turn_id_hash
                WHERE tree.root_session_id = $turn_root_session_id
                  AND {UsagePredicate}
            )
            SELECT
                attributed_events.prompt_turn_id_hash,
                COALESCE(
                    MIN(prompt_turn.started_at_unix_ms),
                    MIN(attributed_events.occurred_at_unix_ms)),
                MAX(attributed_events.occurred_at_unix_ms),
                COUNT(*),
                MAX(prompt_turn.prompt_preview),
                COALESCE(MAX(prompt_turn.user_message_count), 0),
                COALESCE(SUM((
                    SELECT COUNT(*)
                    FROM usage_event_tools AS tools
                    WHERE tools.agent_id = attributed_events.agent_id
                      AND tools.source_instance_id =
                          attributed_events.source_instance_id
                      AND tools.event_dedup_key =
                          attributed_events.dedup_key
                )), 0),
                MAX(COALESCE(SUM(
                    attributed_events.normalized_total_value), 0)) OVER (),
                {AllMetrics("attributed_events")},
                {Pricing("attributed_events")}
            FROM attributed_events
            LEFT JOIN usage_turns AS prompt_turn
                ON prompt_turn.agent_id = attributed_events.agent_id
               AND prompt_turn.source_instance_id =
                   attributed_events.source_instance_id
               AND prompt_turn.session_id = $turn_root_session_id
               AND prompt_turn.turn_id_hash =
                   attributed_events.prompt_turn_id_hash
            WHERE attributed_events.prompt_turn_id_hash IS NOT NULL
            GROUP BY attributed_events.prompt_turn_id_hash
            ORDER BY COALESCE(
                         MIN(prompt_turn.started_at_unix_ms),
                         MIN(attributed_events.occurred_at_unix_ms)) DESC,
                     attributed_events.prompt_turn_id_hash ASC
            LIMIT $limit OFFSET $offset;
            """;
        BindFilter(command, scopedFilter);
        command.Parameters.AddWithValue("$turn_root_session_id", rootSessionId);
        command.Parameters.AddWithValue("$limit", filter.Limit);
        command.Parameters.AddWithValue("$offset", filter.Offset);

        var turns = new List<TurnUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            turns.Add(new TurnUsageRow(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetInt64(3),
                ReadMetricSet(reader, 8))
            {
                PromptPreview = reader.IsDBNull(4) ? null : reader.GetString(4),
                UserMessageCount = reader.GetInt32(5),
                ToolCallCount = reader.GetInt64(6),
                MaxPromptTokens = reader.GetInt64(7),
                Pricing = ReadPricingAggregate(reader, 44)
            });
        }

        (long totalCalls, long attributedCalls, long promptTurnCount) =
            await ReadTurnCoverageAsync(
                connection,
                scopedFilter,
                identity.RootSessionId,
                cancellationToken);
        UnattributedUsageSummary unattributed = await ReadUnattributedAsync(
            connection,
            scopedFilter,
            identity.RootSessionId,
            cancellationToken);
        TurnCoverageStatus coverage = totalCalls == 0
            ? TurnCoverageStatus.NoData
            : attributedCalls == totalCalls
                ? TurnCoverageStatus.Complete
                : attributedCalls == 0
                    ? TurnCoverageStatus.Unsupported
                    : TurnCoverageStatus.Partial;
        return new TurnUsagePage(
            coverage,
            turns,
            unattributed,
            promptTurnCount);
    }

    public async Task<IReadOnlyList<TurnCallUsageRow>> GetTurnCallsAsync(
        UsageFilter filter,
        RootSessionIdentity identity,
        string turnIdHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnIdHash);
        string rootSessionId = identity.RootSessionId;
        UsageFilter scopedFilter = WithRootSession(filter, identity);

        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte},
            attributed_events AS (
                SELECT
                    usage_events.*,
                    tree.session_kind,
                    tree.session_role,
                    CASE
                        WHEN attribution.attribution_state = 0
                         AND attribution.origin_session_id =
                             $turn_root_session_id
                        THEN attribution.origin_turn_id_hash
                        WHEN usage_events.session_id = $turn_root_session_id
                        THEN usage_events.turn_id_hash
                        ELSE NULL
                    END AS prompt_turn_id_hash
                FROM usage_events
                INNER JOIN session_tree AS tree
                    ON tree.agent_id = usage_events.agent_id
                   AND tree.source_instance_id =
                       usage_events.source_instance_id
                   AND tree.session_id = usage_events.session_id
                LEFT JOIN usage_turn_attributions AS attribution
                    ON attribution.agent_id = usage_events.agent_id
                   AND attribution.source_instance_id =
                       usage_events.source_instance_id
                   AND attribution.session_id = usage_events.session_id
                   AND attribution.turn_id_hash = usage_events.turn_id_hash
                WHERE tree.root_session_id = $turn_root_session_id
                  AND {UsagePredicate}
            )
            SELECT
                attributed_events.occurred_at_unix_ms,
                COALESCE(attributed_events.normalized_model, '未知模型'),
                attributed_events.session_id,
                attributed_events.session_kind,
                attributed_events.session_role,
                (
                    SELECT GROUP_CONCAT(ordered_tools.tool_name, CHAR(31))
                    FROM (
                        SELECT tools.tool_name
                        FROM usage_event_tools AS tools
                        WHERE tools.agent_id = attributed_events.agent_id
                          AND tools.source_instance_id =
                              attributed_events.source_instance_id
                          AND tools.event_dedup_key =
                              attributed_events.dedup_key
                        ORDER BY tools.ordinal
                    ) AS ordered_tools
                ),
                {AllMetrics("attributed_events")},
                {Pricing("attributed_events")}
            FROM attributed_events
            WHERE attributed_events.prompt_turn_id_hash = $turn_id_hash
            GROUP BY attributed_events.agent_id,
                     attributed_events.source_instance_id,
                     attributed_events.dedup_key
            ORDER BY attributed_events.occurred_at_unix_ms,
                     attributed_events.dedup_key;
            """;
        BindFilter(command, scopedFilter);
        command.Parameters.AddWithValue("$turn_root_session_id", rootSessionId);
        command.Parameters.AddWithValue("$turn_id_hash", turnIdHash);

        var rows = new List<TurnCallUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            IReadOnlyList<string> tools = reader.IsDBNull(5)
                ? []
                : reader.GetString(5)
                    .Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
            rows.Add(new TurnCallUsageRow(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.GetString(1),
                reader.GetString(2),
                (SessionKind)reader.GetInt32(3),
                (SessionRole)reader.GetInt32(4),
                tools,
                ReadMetricSet(reader, 6))
            {
                Pricing = ReadPricingAggregate(reader, 42)
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<SourceStatusRow>> GetSourcesAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                instances.source_instance_id,
                cursors.source_entity_id,
                instances.agent_id,
                instances.source_kind,
                instances.display_name,
                instances.root_path,
                cursors.source_path,
                cursors.parser_version,
                cursors.last_success_unix_ms,
                cursors.last_error,
                cursors.last_error_unix_ms,
                instances.compatibility_level,
                instances.compatibility_code,
                instances.requires_rescan
            FROM source_instances AS instances
            INNER JOIN source_cursors AS cursors
                ON cursors.source_instance_id = instances.source_instance_id
            ORDER BY instances.display_name COLLATE NOCASE ASC,
                     instances.source_instance_id ASC,
                     cursors.source_entity_id ASC;
            """;

        var sources = new List<SourceStatusRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new SourceStatusRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                (SourceKind)reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ReadNullableTimestamp(reader, 8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ReadNullableTimestamp(reader, 10))
            {
                CompatibilityLevel = (CompatibilityLevel)reader.GetInt32(11),
                CompatibilityCode = reader.IsDBNull(12) ? null : reader.GetString(12),
                RequiresRescan = reader.GetInt32(13) != 0
            });
        }

        return sources;
    }

    private static async Task<Dictionary<string, List<SessionModelUsageRow>>>
        ReadSessionModelsAsync(
            SqliteConnection connection,
            UsageFilter filter,
            string rootSessionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte}
            SELECT
                tree.session_id,
                COALESCE(usage_events.normalized_model, '未知模型'),
                COUNT(*),
                {AllMetrics("usage_events")},
                {Pricing("usage_events")}
            FROM usage_events
            INNER JOIN session_tree AS tree
                ON tree.agent_id = usage_events.agent_id
               AND tree.source_instance_id = usage_events.source_instance_id
               AND tree.session_id = usage_events.session_id
            WHERE tree.root_session_id = $model_root_session_id
              AND {UsagePredicate}
            GROUP BY tree.session_id,
                     COALESCE(usage_events.normalized_model, '未知模型')
            ORDER BY tree.session_id ASC,
                     COALESCE(usage_events.normalized_model, '未知模型')
                         COLLATE NOCASE ASC;
            """;
        BindFilter(command, filter);
        command.Parameters.AddWithValue("$model_root_session_id", rootSessionId);

        var result = new Dictionary<string, List<SessionModelUsageRow>>(
            StringComparer.Ordinal);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string sessionId = reader.GetString(0);
            if (!result.TryGetValue(
                    sessionId,
                    out List<SessionModelUsageRow>? models))
            {
                models = [];
                result.Add(sessionId, models);
            }

            models.Add(new SessionModelUsageRow(
                reader.GetString(1),
                reader.GetInt64(2),
                ReadMetricSet(reader, 3))
            {
                Pricing = ReadPricingAggregate(reader, 39)
            });
        }

        return result;
    }

    private static async Task<IReadOnlyList<SessionModelUsageRow>>
        ReadRootModelsAsync(
            SqliteConnection connection,
            UsageFilter filter,
            string rootSessionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte}
            SELECT
                COALESCE(usage_events.normalized_model, '未知模型'),
                COUNT(*),
                {AllMetrics("usage_events")},
                {Pricing("usage_events")}
            FROM usage_events
            INNER JOIN session_tree AS tree
                ON tree.agent_id = usage_events.agent_id
               AND tree.source_instance_id = usage_events.source_instance_id
               AND tree.session_id = usage_events.session_id
            WHERE tree.root_session_id = $model_root_session_id
              AND {UsagePredicate}
            GROUP BY COALESCE(usage_events.normalized_model, '未知模型')
            ORDER BY SUM(COALESCE(
                         usage_events.normalized_total_value, 0)) DESC,
                     COALESCE(usage_events.normalized_model, '未知模型')
                         COLLATE NOCASE ASC;
            """;
        BindFilter(command, filter);
        command.Parameters.AddWithValue("$model_root_session_id", rootSessionId);

        var rows = new List<SessionModelUsageRow>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SessionModelUsageRow(
                reader.GetString(0),
                reader.GetInt64(1),
                ReadMetricSet(reader, 2))
            {
                Pricing = ReadPricingAggregate(reader, 38)
            });
        }

        return rows;
    }

    private static async Task<(
        long TotalCalls,
        long AttributedCalls,
        long PromptTurnCount)>
        ReadTurnCoverageAsync(
            SqliteConnection connection,
            UsageFilter filter,
            string rootSessionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte},
            attributed_events AS (
                SELECT
                    usage_events.*,
                    CASE
                        WHEN attribution.attribution_state = 0
                         AND attribution.origin_session_id =
                             $coverage_root_session_id
                        THEN attribution.origin_turn_id_hash
                        WHEN usage_events.session_id =
                             $coverage_root_session_id
                        THEN usage_events.turn_id_hash
                        ELSE NULL
                    END AS prompt_turn_id_hash
                FROM usage_events
                INNER JOIN session_tree AS tree
                    ON tree.agent_id = usage_events.agent_id
                   AND tree.source_instance_id =
                       usage_events.source_instance_id
                   AND tree.session_id = usage_events.session_id
                LEFT JOIN usage_turn_attributions AS attribution
                    ON attribution.agent_id = usage_events.agent_id
                   AND attribution.source_instance_id =
                       usage_events.source_instance_id
                   AND attribution.session_id = usage_events.session_id
                   AND attribution.turn_id_hash = usage_events.turn_id_hash
                WHERE tree.root_session_id = $coverage_root_session_id
                  AND {UsagePredicate}
            )
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE
                    WHEN attributed_events.prompt_turn_id_hash IS NOT NULL
                    THEN 1 ELSE 0
                END), 0),
                COUNT(DISTINCT attributed_events.prompt_turn_id_hash)
            FROM attributed_events;
            """;
        BindFilter(command, filter);
        command.Parameters.AddWithValue(
            "$coverage_root_session_id",
            rootSessionId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "SQLite did not return turn coverage.");
        }

        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private static async Task<UnattributedUsageSummary> ReadUnattributedAsync(
        SqliteConnection connection,
        UsageFilter filter,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            {SessionTreeCte},
            attributed_events AS (
                SELECT
                    usage_events.*,
                    CASE
                        WHEN attribution.attribution_state = 0
                         AND attribution.origin_session_id =
                             $unattributed_root_session_id
                        THEN attribution.origin_turn_id_hash
                        WHEN usage_events.session_id =
                             $unattributed_root_session_id
                        THEN usage_events.turn_id_hash
                        ELSE NULL
                    END AS prompt_turn_id_hash
                FROM usage_events
                INNER JOIN session_tree AS tree
                    ON tree.agent_id = usage_events.agent_id
                   AND tree.source_instance_id =
                       usage_events.source_instance_id
                   AND tree.session_id = usage_events.session_id
                LEFT JOIN usage_turn_attributions AS attribution
                    ON attribution.agent_id = usage_events.agent_id
                   AND attribution.source_instance_id =
                       usage_events.source_instance_id
                   AND attribution.session_id = usage_events.session_id
                   AND attribution.turn_id_hash = usage_events.turn_id_hash
                WHERE tree.root_session_id = $unattributed_root_session_id
                  AND {UsagePredicate}
            )
            SELECT
                COUNT(*),
                {AllMetrics("attributed_events")},
                {Pricing("attributed_events")}
            FROM attributed_events
            WHERE attributed_events.prompt_turn_id_hash IS NULL;
            """;
        BindFilter(command, filter);
        command.Parameters.AddWithValue(
            "$unattributed_root_session_id",
            rootSessionId);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "SQLite did not return unattributed usage.");
        }

        return new UnattributedUsageSummary(
            reader.GetInt64(0),
            ReadMetricSet(reader, 1))
        {
            Pricing = ReadPricingAggregate(reader, 37)
        };
    }

    private static UsageFilter WithRootSession(
        UsageFilter filter,
        RootSessionIdentity identity) => new(
            filter.StartInclusiveUtc,
            filter.EndExclusiveUtc,
            filter.AgentId,
            filter.NormalizedModel,
            filter.Limit,
            filter.Offset,
            filter.ProjectId,
            identity.RootSessionId,
            filter.UnidentifiedProjectOnly,
            identity);

    private static string AllMetrics(string tablePrefix) => string.Join(
        "," + Environment.NewLine,
        new[]
        {
            "input_reported",
            "uncached_input",
            "cache_read",
            "cache_write",
            "output",
            "reasoning",
            "tool",
            "reported_total",
            "normalized_total"
        }.Select(name => Metric($"{tablePrefix}.{name}")));

    private static UsageMetricSet ReadMetricSet(
        SqliteDataReader reader,
        int startIndex) => new(
            ReadAggregate(reader, startIndex),
            ReadAggregate(reader, startIndex + 4),
            ReadAggregate(reader, startIndex + 8),
            ReadAggregate(reader, startIndex + 12),
            ReadAggregate(reader, startIndex + 16),
            ReadAggregate(reader, startIndex + 20),
            ReadAggregate(reader, startIndex + 24),
            ReadAggregate(reader, startIndex + 28),
            ReadAggregate(reader, startIndex + 32));

    private static string Metric(string columnPrefix) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            MetricSelect,
            columnPrefix);

    private static string Pricing(string tablePrefix) =>
        string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            PricingSelect,
            tablePrefix);

    private static void BindFilter(SqliteCommand command, UsageFilter filter)
    {
        command.Parameters.AddWithValue(
            "$start",
            filter.StartInclusiveUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$end",
            filter.EndExclusiveUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "$agent_id",
            (object?)filter.AgentId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$normalized_model",
            (object?)filter.NormalizedModel ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$project_id",
            (object?)filter.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$root_session_id",
            (object?)filter.RootSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$root_agent_id",
            (object?)filter.RootIdentity?.AgentId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$root_source_instance_id",
            (object?)filter.RootIdentity?.SourceInstanceId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$unidentified_project_only",
            filter.UnidentifiedProjectOnly ? 1 : 0);
    }

    private static MetricAggregate ReadAggregate(
        SqliteDataReader reader,
        int startIndex)
    {
        return new MetricAggregate(
            ReadNullableInt64(reader, startIndex),
            checked((int)reader.GetInt64(startIndex + 1)),
            checked((int)reader.GetInt64(startIndex + 2)),
            checked((int)reader.GetInt64(startIndex + 3)));
    }

    private static PricingAggregate ReadPricingAggregate(
        SqliteDataReader reader,
        int startIndex) => new(
        reader.IsDBNull(startIndex)
            ? null
            : decimal.Round(
                Convert.ToDecimal(
                    reader.GetValue(startIndex),
                    System.Globalization.CultureInfo.InvariantCulture),
                12),
        checked((int)reader.GetInt64(startIndex + 1)),
        checked((int)reader.GetInt64(startIndex + 2)),
        checked((int)reader.GetInt64(startIndex + 3)),
        (PricingMissingCategory)checked((int)reader.GetInt64(startIndex + 4)));

    private static EventPriceEstimate ReadEventPrice(
        SqliteDataReader reader,
        int startIndex) => new(
        (EventPricingStatus)reader.GetInt32(startIndex),
        ReadNullableDecimal(reader, startIndex + 1),
        (PricingMissingCategory)reader.GetInt32(startIndex + 2),
        reader.IsDBNull(startIndex + 3)
            ? null
            : reader.GetString(startIndex + 3),
        reader.IsDBNull(startIndex + 4)
            ? null
            : reader.GetString(startIndex + 4),
        ReadNullableDecimal(reader, startIndex + 5),
        ReadNullableDecimal(reader, startIndex + 6),
        ReadNullableDecimal(reader, startIndex + 7),
        ReadNullableDecimal(reader, startIndex + 8),
        ReadNullableDecimal(reader, startIndex + 9),
        ReadNullableDecimal(reader, startIndex + 10));

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteConnection connection,
        string sql,
        UsageFilter filter,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        BindFilter(command, filter);

        var values = new List<string>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private enum FilterFacet
    {
        Agent,
        Model,
        Project
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetInt64(index);

    private static decimal? ReadNullableDecimal(
        SqliteDataReader reader,
        int index) =>
        reader.IsDBNull(index)
            ? null
            : decimal.Parse(
                reader.GetString(index),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture);

    private static decimal ReadRequiredDecimal(
        SqliteDataReader reader,
        int index) =>
        ReadNullableDecimal(reader, index) ??
        throw new InvalidDataException("Stored price rate is missing.");

    private static PriceSettingAccumulator GetOrCreate(
        IDictionary<string, PriceSettingAccumulator> rows,
        string normalizedModel)
    {
        var validated = new ModelPriceRate(
            normalizedModel,
            0m,
            null,
            null,
            0m);
        if (!rows.TryGetValue(
                validated.NormalizedModel,
                out PriceSettingAccumulator? row))
        {
            row = new PriceSettingAccumulator();
            rows.Add(validated.NormalizedModel, row);
        }

        return row;
    }

    private static DateTimeOffset? ReadNullableTimestamp(
        SqliteDataReader reader,
        int index) =>
        reader.IsDBNull(index)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));

    private sealed class PriceSettingAccumulator
    {
        public ModelPriceRate? BuiltInRate { get; set; }

        public ModelPriceRate? CustomRate { get; set; }

        public long ObservedRecords { get; set; }
    }
}
