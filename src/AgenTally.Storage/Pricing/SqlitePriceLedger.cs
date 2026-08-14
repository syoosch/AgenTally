using System.Globalization;
using AgenTally.Domain.Usage;
using AgenTally.Storage.Database;
using Microsoft.Data.Sqlite;

namespace AgenTally.Storage.Pricing;

public sealed class SqlitePriceLedger : IPriceLedger
{
    private readonly SqliteConnectionFactory _connections;
    private readonly OfflinePriceCatalog _catalog;

    public SqlitePriceLedger(
        SqliteConnectionFactory connections,
        OfflinePriceCatalog? catalog = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _catalog = catalog ?? OfflinePriceCatalog.Default;
    }

    public IReadOnlyList<ResolvedPriceRule> GetBuiltInCatalog() =>
        _catalog.Entries;

    public async Task<IReadOnlyList<CustomPriceSetting>> GetCustomPricesAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await _connections.OpenReaderAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                normalized_model,
                input_rate_usd_per_million,
                cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million,
                output_rate_usd_per_million,
                long_context_threshold_tokens,
                long_context_input_multiplier,
                long_context_output_multiplier,
                updated_at_unix_ms
            FROM pricing_overrides
            ORDER BY normalized_model;
            """;
        var result = new List<CustomPriceSetting>();
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CustomPriceSetting(
                new ModelPriceRate(
                    reader.GetString(0),
                    ReadDecimal(reader, 1),
                    ReadNullableDecimal(reader, 2),
                    ReadNullableDecimal(reader, 3),
                    ReadDecimal(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    ReadDecimal(reader, 6),
                    ReadDecimal(reader, 7)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8))));
        }

        return result;
    }

    public async Task<int> SetCustomPriceAsync(
        ModelPriceRate rate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rate);
        rate = CanonicalizeOverrideRate(rate);
        await using SqliteConnection connection =
            await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pricing_overrides (
                    normalized_model,
                    input_rate_usd_per_million,
                    cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million,
                    output_rate_usd_per_million,
                    long_context_threshold_tokens,
                    long_context_input_multiplier,
                    long_context_output_multiplier,
                    updated_at_unix_ms
                ) VALUES (
                    $normalized_model,
                    $input_rate,
                    $cached_input_rate,
                    $cache_write_rate,
                    $output_rate,
                    $threshold,
                    $input_multiplier,
                    $output_multiplier,
                    $updated_at
                )
                ON CONFLICT(normalized_model) DO UPDATE SET
                    input_rate_usd_per_million = excluded.input_rate_usd_per_million,
                    cached_input_rate_usd_per_million =
                        excluded.cached_input_rate_usd_per_million,
                    cache_write_rate_usd_per_million =
                        excluded.cache_write_rate_usd_per_million,
                    output_rate_usd_per_million =
                        excluded.output_rate_usd_per_million,
                    long_context_threshold_tokens =
                        excluded.long_context_threshold_tokens,
                    long_context_input_multiplier =
                        excluded.long_context_input_multiplier,
                    long_context_output_multiplier =
                        excluded.long_context_output_multiplier,
                    updated_at_unix_ms = excluded.updated_at_unix_ms;
                """;
            command.Parameters.AddWithValue(
                "$normalized_model",
                rate.NormalizedModel);
            AddDecimal(command, "$input_rate", rate.InputUsdPerMillion);
            AddNullableDecimal(
                command,
                "$cached_input_rate",
                rate.CachedInputUsdPerMillion);
            AddNullableDecimal(
                command,
                "$cache_write_rate",
                rate.CacheWriteUsdPerMillion);
            AddDecimal(command, "$output_rate", rate.OutputUsdPerMillion);
            command.Parameters.AddWithValue(
                "$threshold",
                (object?)rate.LongContextThresholdTokens ?? DBNull.Value);
            AddDecimal(
                command,
                "$input_multiplier",
                rate.LongContextInputMultiplier);
            AddDecimal(
                command,
                "$output_multiplier",
                rate.LongContextOutputMultiplier);
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        int priced = await SqlitePriceBinder.BindStoredUnpricedAsync(
            connection,
            transaction,
            _catalog,
            rate.NormalizedModel,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return priced;
    }

    public async Task<int> RestoreDefaultAsync(
        string normalizedModel,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedModel);
        string model = ModelIdentityCanonicalizer.Canonicalize(
            normalizedModel)!;
        await using SqliteConnection connection =
            await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM pricing_overrides
                WHERE normalized_model = $normalized_model;
                """;
            command.Parameters.AddWithValue("$normalized_model", model);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        int priced = await SqlitePriceBinder.BindStoredUnpricedAsync(
            connection,
            transaction,
            _catalog,
            model,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return priced;
    }

    public async Task<int> RestoreAllDefaultsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM pricing_overrides;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        int priced = await SqlitePriceBinder.BindStoredUnpricedAsync(
            connection,
            transaction,
            _catalog,
            normalizedModel: null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return priced;
    }

    public async Task ReplaceCustomPricesAsync(
        IReadOnlyList<CustomPriceSetting> settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using SqliteConnection connection =
            await OpenInitializedAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM pricing_overrides;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (CustomPriceSetting setting in settings)
        {
            await InsertSettingAsync(
                connection,
                transaction,
                setting,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenInitializedAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection =
            await _connections.OpenWriterAsync(cancellationToken);
        try
        {
            await DatabaseSchema.InitializeAsync(connection, cancellationToken);
            await SqlitePriceBinder.ApplyCatalogUpgradeAsync(
                connection,
                _catalog,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CustomPriceSetting setting,
        CancellationToken cancellationToken)
    {
        ModelPriceRate rate = CanonicalizeOverrideRate(setting.Rate);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pricing_overrides (
                normalized_model,
                input_rate_usd_per_million,
                cached_input_rate_usd_per_million,
                cache_write_rate_usd_per_million,
                output_rate_usd_per_million,
                long_context_threshold_tokens,
                long_context_input_multiplier,
                long_context_output_multiplier,
                updated_at_unix_ms
            ) VALUES (
                $normalized_model,
                $input_rate,
                $cached_input_rate,
                $cache_write_rate,
                $output_rate,
                $threshold,
                $input_multiplier,
                $output_multiplier,
                $updated_at
            );
            """;
        command.Parameters.AddWithValue("$normalized_model", rate.NormalizedModel);
        AddDecimal(command, "$input_rate", rate.InputUsdPerMillion);
        AddNullableDecimal(
            command,
            "$cached_input_rate",
            rate.CachedInputUsdPerMillion);
        AddNullableDecimal(
            command,
            "$cache_write_rate",
            rate.CacheWriteUsdPerMillion);
        AddDecimal(command, "$output_rate", rate.OutputUsdPerMillion);
        command.Parameters.AddWithValue(
            "$threshold",
            (object?)rate.LongContextThresholdTokens ?? DBNull.Value);
        AddDecimal(
            command,
            "$input_multiplier",
            rate.LongContextInputMultiplier);
        AddDecimal(
            command,
            "$output_multiplier",
            rate.LongContextOutputMultiplier);
        command.Parameters.AddWithValue(
            "$updated_at",
            setting.UpdatedAtUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ModelPriceRate CanonicalizeOverrideRate(ModelPriceRate rate)
    {
        string canonical = ModelIdentityCanonicalizer.Canonicalize(
            rate.NormalizedModel)!;
        return string.Equals(
                canonical,
                rate.NormalizedModel,
                StringComparison.Ordinal)
            ? rate
            : new ModelPriceRate(
                canonical,
                rate.InputUsdPerMillion,
                rate.CachedInputUsdPerMillion,
                rate.CacheWriteUsdPerMillion,
                rate.OutputUsdPerMillion,
                rate.LongContextThresholdTokens,
                rate.LongContextInputMultiplier,
                rate.LongContextOutputMultiplier);
    }

    private static decimal ReadDecimal(SqliteDataReader reader, int index) =>
        decimal.Parse(
            reader.GetString(index),
            NumberStyles.Number,
            CultureInfo.InvariantCulture);

    private static decimal? ReadNullableDecimal(
        SqliteDataReader reader,
        int index) =>
        reader.IsDBNull(index) ? null : ReadDecimal(reader, index);

    private static void AddDecimal(
        SqliteCommand command,
        string name,
        decimal value) =>
        command.Parameters.AddWithValue(
            name,
            value.ToString(CultureInfo.InvariantCulture));

    private static void AddNullableDecimal(
        SqliteCommand command,
        string name,
        decimal? value) =>
        command.Parameters.AddWithValue(
            name,
            value.HasValue
                ? value.Value.ToString(CultureInfo.InvariantCulture)
                : DBNull.Value);
}
