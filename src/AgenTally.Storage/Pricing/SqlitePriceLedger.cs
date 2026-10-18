using System.Globalization;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Database;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Pricing;

public sealed class SqlitePriceLedger : IPriceLedger
{
    private readonly SqliteConnectionFactory _connections;
    private readonly OfflinePriceCatalog _catalog;

    public SqlitePriceLedger(SqliteConnectionFactory connections, OfflinePriceCatalog? catalog = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _catalog = catalog ?? OfflinePriceCatalog.Default;
    }

    public IReadOnlyList<ResolvedPriceRule> GetBuiltInCatalog() => _catalog.Entries;

    public async Task<IReadOnlyList<CustomPriceSetting>> GetCustomPricesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PriceOverridePeriod> periods = await GetOverridePeriodsAsync(cancellationToken);
        return periods.GroupBy(static period => period.NormalizedModel)
            .Select(static group => group.MaxBy(static period => period.EffectiveFromUtc)!)
            .Where(static period => period.Rate is not null)
            .Select(static period => new CustomPriceSetting(period.Rate!, period.UpdatedAtUtc,
                period.EffectiveFromUtc)).ToArray();
    }

    public async Task<IReadOnlyList<PriceOverridePeriod>> GetOverridePeriodsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _connections.OpenReaderAsync(cancellationToken);
        return await ReadPeriodsAsync(connection, null, cancellationToken);
    }

    // Undated callers retain the legacy all-time baseline. New UI/IPC commands
    // always supply an explicit start; migration never invents one from updated_at.
    public Task<int> SetCustomPriceAsync(ModelPriceRate rate, CancellationToken cancellationToken) =>
        SetCustomPriceAsync(rate, DateTimeOffset.MinValue, cancellationToken);

    public Task<int> SetCustomPriceAsync(ModelPriceRate rate, DateTimeOffset effectiveFromUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rate);
        ModelPriceRate canonical = CanonicalizeOverrideRate(rate);
        return ChangeAsync(canonical.NormalizedModel, canonical, effectiveFromUtc, cancellationToken);
    }

    public Task<int> RestoreDefaultAsync(string normalizedModel, CancellationToken cancellationToken) =>
        RestoreDefaultAsync(normalizedModel, DateTimeOffset.MinValue, cancellationToken);

    public Task<int> RestoreDefaultAsync(string normalizedModel, DateTimeOffset effectiveFromUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedModel);
        return ChangeAsync(ModelIdentityCanonicalizer.Canonicalize(normalizedModel)!, null,
            effectiveFromUtc, cancellationToken);
    }

    public Task<int> RestoreAllDefaultsAsync(CancellationToken cancellationToken) =>
        RestoreAllDefaultsAsync(DateTimeOffset.MinValue, cancellationToken);

    public async Task<int> RestoreAllDefaultsAsync(DateTimeOffset effectiveFromUtc,
        CancellationToken cancellationToken)
    {
        ValidateInstant(effectiveFromUtc);
        await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        IReadOnlyList<PriceOverridePeriod> periods = await ReadPeriodsAsync(connection, transaction, cancellationToken);
        foreach (string model in periods.Select(static period => period.NormalizedModel).Distinct(StringComparer.Ordinal))
        {
            await RestoreAsync(connection, transaction, model, effectiveFromUtc, cancellationToken);
        }
        int priced = await SqlitePriceBinder.BindStoredUnpricedAsync(connection, transaction, _catalog, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return priced;
    }

    private async Task<int> ChangeAsync(string model, ModelPriceRate? rate,
        DateTimeOffset from, CancellationToken cancellationToken)
    {
        ValidateInstant(from);
        await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (rate is null)
        {
            await RestoreAsync(connection, transaction, model, from, cancellationToken);
        }
        else
        {
            await InsertAsync(connection, transaction,
                new PriceOverridePeriod(model, from, rate, DateTimeOffset.UtcNow), cancellationToken);
        }
        int priced = await SqlitePriceBinder.BindStoredUnpricedAsync(connection, transaction, _catalog, model, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return priced;
    }

    private static async Task RestoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        string model, DateTimeOffset from, CancellationToken cancellationToken)
    {
        // Restore cancels later scheduled overrides but preserves earlier periods
        // so a late import still receives its historical custom/default policy.
        await using SqliteCommand removeFuture = connection.CreateCommand();
        removeFuture.Transaction = transaction;
        removeFuture.CommandText = "DELETE FROM pricing_overrides WHERE normalized_model=$model AND effective_from_unix_ms >= $from;";
        removeFuture.Parameters.AddWithValue("$model", model);
        removeFuture.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        await removeFuture.ExecuteNonQueryAsync(cancellationToken);
        await InsertAsync(connection, transaction,
            new PriceOverridePeriod(model, from, null, DateTimeOffset.UtcNow), cancellationToken);
    }

    public Task ReplaceCustomPricesAsync(IReadOnlyList<CustomPriceSetting> settings,
        CancellationToken cancellationToken) => ReplaceOverridePeriodsAsync(settings.Select(static setting =>
            new PriceOverridePeriod(setting.Rate.NormalizedModel,
                setting.EffectiveFromUtc ?? DateTimeOffset.MinValue, setting.Rate, setting.UpdatedAtUtc)).ToArray(), cancellationToken);

    public async Task ReplaceOverridePeriodsAsync(IReadOnlyList<PriceOverridePeriod> periods,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(periods);
        await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM pricing_overrides;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        var identities = new HashSet<(string, DateTimeOffset)>();
        foreach (PriceOverridePeriod period in periods)
        {
            ValidateInstant(period.EffectiveFromUtc);
            string model = ModelIdentityCanonicalizer.Canonicalize(period.NormalizedModel)!;
            if (!identities.Add((model, period.EffectiveFromUtc)) ||
                period.Rate is not null && ModelIdentityCanonicalizer.Canonicalize(period.Rate.NormalizedModel) != model)
            {
                throw new InvalidDataException("Conflicting price override periods.");
            }
            await InsertAsync(connection, transaction, period with
            {
                NormalizedModel = model,
                Rate = period.Rate is null ? null : CanonicalizeOverrideRate(period.Rate)
            }, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task<IReadOnlyList<PriceOverridePeriod>> ReadPeriodsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT normalized_model, effective_from_unix_ms, use_default,
                input_rate_usd_per_million, cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million, output_rate_usd_per_million,
                long_context_threshold_tokens, long_context_input_multiplier,
                long_context_output_multiplier, updated_at_unix_ms
            FROM pricing_overrides ORDER BY normalized_model, effective_from_unix_ms;
            """;
        var result = new List<PriceOverridePeriod>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string model = reader.GetString(0);
            ModelPriceRate? rate = reader.GetInt32(2) == 1 ? null : new ModelPriceRate(model,
                ReadDecimal(reader, 3), ReadNullableDecimal(reader, 4), ReadNullableDecimal(reader, 5),
                ReadDecimal(reader, 6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
                ReadDecimal(reader, 8), ReadDecimal(reader, 9));
            result.Add(new PriceOverridePeriod(model,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), rate,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10))));
        }
        return result;
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        PriceOverridePeriod period, CancellationToken cancellationToken)
    {
        ModelPriceRate? rate = period.Rate;
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pricing_overrides (normalized_model, effective_from_unix_ms, use_default,
                input_rate_usd_per_million, cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million, output_rate_usd_per_million,
                long_context_threshold_tokens, long_context_input_multiplier,
                long_context_output_multiplier, updated_at_unix_ms)
            VALUES ($model, $from, $default, $input, $cached, $write, $output,
                $threshold, $input_multiplier, $output_multiplier, $updated)
            ON CONFLICT(normalized_model, effective_from_unix_ms) DO UPDATE SET
                use_default=excluded.use_default,
                input_rate_usd_per_million=excluded.input_rate_usd_per_million,
                cached_input_rate_usd_per_million=excluded.cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million=excluded.cache_write_rate_usd_per_million,
                output_rate_usd_per_million=excluded.output_rate_usd_per_million,
                long_context_threshold_tokens=excluded.long_context_threshold_tokens,
                long_context_input_multiplier=excluded.long_context_input_multiplier,
                long_context_output_multiplier=excluded.long_context_output_multiplier,
                updated_at_unix_ms=excluded.updated_at_unix_ms;
            """;
        command.Parameters.AddWithValue("$model", period.NormalizedModel);
        command.Parameters.AddWithValue("$from", period.EffectiveFromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$default", rate is null ? 1 : 0);
        AddDecimal(command, "$input", rate?.InputUsdPerMillion);
        AddDecimal(command, "$cached", rate?.CachedInputUsdPerMillion);
        AddDecimal(command, "$write", rate?.CacheWriteUsdPerMillion);
        AddDecimal(command, "$output", rate?.OutputUsdPerMillion);
        command.Parameters.AddWithValue("$threshold", (object?)rate?.LongContextThresholdTokens ?? DBNull.Value);
        AddDecimal(command, "$input_multiplier", rate?.LongContextInputMultiplier ?? 1m);
        AddDecimal(command, "$output_multiplier", rate?.LongContextOutputMultiplier ?? 1m);
        command.Parameters.AddWithValue("$updated", period.UpdatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _connections.OpenWriterAsync(cancellationToken);
        try
        {
            await DatabaseSchema.InitializeAsync(connection, cancellationToken);
            await SqlitePriceBinder.ApplyCatalogUpgradeAsync(connection, _catalog, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void ValidateInstant(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Price start must be UTC at millisecond precision.");
        }
    }

    private static ModelPriceRate CanonicalizeOverrideRate(ModelPriceRate rate)
    {
        string canonical = ModelIdentityCanonicalizer.Canonicalize(rate.NormalizedModel)!;
        return canonical == rate.NormalizedModel ? rate : new ModelPriceRate(canonical,
            rate.InputUsdPerMillion, rate.CachedInputUsdPerMillion, rate.CacheWriteUsdPerMillion,
            rate.OutputUsdPerMillion, rate.LongContextThresholdTokens,
            rate.LongContextInputMultiplier, rate.LongContextOutputMultiplier);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, int index) =>
        decimal.Parse(reader.GetString(index), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : ReadDecimal(reader, index);

    private static void AddDecimal(SqliteCommand command, string name, decimal? value) =>
        command.Parameters.AddWithValue(name, (object?)value?.ToString(CultureInfo.InvariantCulture) ?? DBNull.Value);
}
