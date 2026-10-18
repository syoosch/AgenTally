using System.Globalization;
using AgenTally.Domain.Usage;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Pricing;

internal static class SqlitePriceBinder
{
    public static async Task ApplyCatalogUpgradeAsync(
        SqliteConnection connection,
        OfflinePriceCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(catalog);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT catalog_version
                FROM pricing_catalog_state
                WHERE singleton_id = 1;
                """;
            string? appliedVersion =
                await current.ExecuteScalarAsync(cancellationToken) as string;
            if (string.Equals(
                    appliedVersion,
                    catalog.Version,
                    StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await BindStoredUnpricedAsync(
            connection,
            transaction,
            catalog,
            normalizedModel: null,
            cancellationToken);
        await using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                INSERT INTO pricing_catalog_state (
                    singleton_id,
                    catalog_version,
                    applied_at_unix_ms
                ) VALUES (
                    1,
                    $catalog_version,
                    $applied_at
                )
                ON CONFLICT(singleton_id) DO UPDATE SET
                    catalog_version = excluded.catalog_version,
                    applied_at_unix_ms = excluded.applied_at_unix_ms;
                """;
            update.Parameters.AddWithValue("$catalog_version", catalog.Version);
            update.Parameters.AddWithValue(
                "$applied_at",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task BindBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UsageEvent> events,
        OfflinePriceCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, IReadOnlyList<PriceOverridePeriod>> overrides =
            await ReadOverridesAsync(connection, transaction, cancellationToken);
        foreach (UsageEvent usageEvent in events)
        {
            ResolvedPriceRule? rule = Resolve(
                ModelIdentityCanonicalizer.Canonicalize(
                    usageEvent.Model.NormalizedModel,
                    usageEvent.AgentId,
                    usageEvent.Model.ProviderId),
                overrides,
                catalog,
                usageEvent.OccurredAtUtc);
            EventPriceEstimate estimate =
                PriceEstimator.Estimate(
                    usageEvent.Tokens, rule, usageEvent.OccurredAtUtc);
            await UpdateEventAsync(
                connection,
                transaction,
                usageEvent.AgentId,
                usageEvent.SourceInstanceId,
                usageEvent.DedupKey,
                estimate,
                cancellationToken);
        }
    }

    public static async Task<int> BindStoredUnpricedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OfflinePriceCatalog catalog,
        string? normalizedModel,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IReadOnlyList<PriceOverridePeriod>> overrides =
            await ReadOverridesAsync(connection, transaction, cancellationToken);
        var events = new List<StoredPricingEvent>();
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT
                    agent_id,
                    source_instance_id,
                    dedup_key,
                    normalized_model,
                    input_reported_value,
                    input_reported_origin,
                    uncached_input_value,
                    uncached_input_origin,
                    cache_read_value,
                    cache_read_origin,
                    cache_write_value,
                    cache_write_origin,
                    output_value,
                    output_origin,
                    cache_included_in_input,
                    reasoning_included_in_output,
                    occurred_at_unix_ms
                FROM usage_events
                WHERE pricing_status = 0
                  AND (
                      $normalized_model IS NULL
                      OR normalized_model = $normalized_model
                  )
                ORDER BY agent_id, source_instance_id, dedup_key;
                """;
            select.Parameters.AddWithValue(
                "$normalized_model",
                (object?)normalizedModel ?? DBNull.Value);
            await using SqliteDataReader reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new StoredPricingEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)),
                    new TokenUsage
                    {
                        InputReported = ReadMetric(reader, 4),
                        UncachedInput = ReadMetric(reader, 6),
                        CacheRead = ReadMetric(reader, 8),
                        CacheWrite = ReadMetric(reader, 10),
                        Output = ReadMetric(reader, 12),
                        CacheIncludedInInput =
                            (MetricInclusion)reader.GetInt32(14),
                        ReasoningIncludedInOutput =
                            (MetricInclusion)reader.GetInt32(15)
                    }));
            }
        }

        int priced = 0;
        foreach (StoredPricingEvent usageEvent in events)
        {
            EventPriceEstimate estimate = PriceEstimator.Estimate(
                usageEvent.Tokens,
                Resolve(
                    usageEvent.NormalizedModel,
                    overrides,
                    catalog,
                    usageEvent.OccurredAtUtc),
                usageEvent.OccurredAtUtc);
            priced += await UpdateEventAsync(
                connection,
                transaction,
                usageEvent.AgentId,
                usageEvent.SourceInstanceId,
                usageEvent.DedupKey,
                estimate,
                cancellationToken);
        }

        return priced;
    }

    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<PriceOverridePeriod>>>
        ReadOverridesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<PriceOverridePeriod> periods = await SqlitePriceLedger.ReadPeriodsAsync(
            connection, transaction, cancellationToken);
        return periods.GroupBy(static period => period.NormalizedModel, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key,
                static group => (IReadOnlyList<PriceOverridePeriod>)group
                    .OrderByDescending(static period => period.EffectiveFromUtc).ToArray(),
                StringComparer.Ordinal);
    }

    private static ResolvedPriceRule? Resolve(
        string? normalizedModel,
        IReadOnlyDictionary<string, IReadOnlyList<PriceOverridePeriod>> overrides,
        OfflinePriceCatalog catalog,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            return null;
        }

        string key = ModelPriceRate.NormalizeModel(normalizedModel);
        if (overrides.TryGetValue(key, out IReadOnlyList<PriceOverridePeriod>? periods))
        {
            for (int index = 0; index < periods.Count; index++)
            {
                PriceOverridePeriod period = periods[index];
                if (period.EffectiveFromUtc > occurredAtUtc)
                {
                    continue;
                }
                if (period.Rate is { } custom)
                {
                    bool legacy = period.EffectiveFromUtc == DateTimeOffset.MinValue;
                    return new ResolvedPriceRule(legacy ? "user-v1" : "user-v2",
                        legacy ? $"user:{key}" : $"user:{key}/{period.EffectiveFromUtc.ToUnixTimeMilliseconds()}",
                        key, custom,
                        EffectiveFromUtc: legacy ? null : period.EffectiveFromUtc,
                        EffectiveUntilUtc: index > 0 ? periods[index - 1].EffectiveFromUtc : null);
                }
                // A default marker ends prior custom rates; it is not a hole
                // through which an older custom period can take precedence.
                break;
            }
        }

        return catalog.TryResolve(key, occurredAtUtc, out ResolvedPriceRule? builtIn)
            ? builtIn
            : null;
    }

    private static async Task<int> UpdateEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string agentId,
        string sourceInstanceId,
        string dedupKey,
        EventPriceEstimate estimate,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE usage_events
            SET price_catalog_version = $catalog_version,
                price_rule_id = $rule_id,
                input_rate_usd_per_million = $input_rate,
                cached_input_rate_usd_per_million = $cached_input_rate,
                cache_write_rate_usd_per_million = $cache_write_rate,
                output_rate_usd_per_million = $output_rate,
                price_context_multiplier = $input_multiplier,
                output_price_context_multiplier = $output_multiplier,
                estimated_cost_usd = $estimated_cost,
                pricing_status = $pricing_status,
                pricing_missing_categories = $missing_categories
            WHERE agent_id = $agent_id
              AND source_instance_id = $source_instance_id
              AND dedup_key = $dedup_key
              AND pricing_status = 0;
            """;
        AddNullable(update, "$catalog_version", estimate.CatalogVersion);
        AddNullable(update, "$rule_id", estimate.RuleId);
        AddNullableDecimal(
            update,
            "$input_rate",
            estimate.InputUsdPerMillion);
        AddNullableDecimal(
            update,
            "$cached_input_rate",
            estimate.CachedInputUsdPerMillion);
        AddNullableDecimal(
            update,
            "$cache_write_rate",
            estimate.CacheWriteUsdPerMillion);
        AddNullableDecimal(
            update,
            "$output_rate",
            estimate.OutputUsdPerMillion);
        AddNullableDecimal(
            update,
            "$input_multiplier",
            estimate.InputContextMultiplier);
        AddNullableDecimal(
            update,
            "$output_multiplier",
            estimate.OutputContextMultiplier);
        AddNullableDecimal(
            update,
            "$estimated_cost",
            estimate.KnownAmountUsd);
        update.Parameters.AddWithValue(
            "$pricing_status",
            (int)estimate.Status);
        update.Parameters.AddWithValue(
            "$missing_categories",
            (int)estimate.MissingCategories);
        update.Parameters.AddWithValue("$agent_id", agentId);
        update.Parameters.AddWithValue("$source_instance_id", sourceInstanceId);
        update.Parameters.AddWithValue("$dedup_key", dedupKey);
        int affected = await update.ExecuteNonQueryAsync(cancellationToken);
        return estimate.Status == EventPricingStatus.Unpriced
            ? 0
            : affected;
    }

    private static TokenMetric ReadMetric(SqliteDataReader reader, int index) =>
        new(
            reader.IsDBNull(index) ? null : reader.GetInt64(index),
            (MetricOrigin)reader.GetInt32(index + 1));

    private static void AddNullable(
        SqliteCommand command,
        string name,
        object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void AddNullableDecimal(
        SqliteCommand command,
        string name,
        decimal? value) =>
        AddNullable(
            command,
            name,
            value?.ToString(CultureInfo.InvariantCulture));

    private sealed record StoredPricingEvent(
        string AgentId,
        string SourceInstanceId,
        string DedupKey,
        string? NormalizedModel,
        DateTimeOffset OccurredAtUtc,
        TokenUsage Tokens);
}
