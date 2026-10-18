using System.Globalization;
using System.IO;
using AgenTally.Domain.Sources;
using AgenTally.Domain.Usage;
using AgenTally.Storage;
using AgenTally.Storage.Database;
using AgenTally.Storage.Pricing;
using AgenTally.Storage.Queries;
using AgenTally.Storage.Writing;
using AgenTally.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AgenTally.Tests.Storage;

[TestClass]
public sealed class SqlitePriceLedgerTests
{
    [TestMethod]
    [DataRow("gpt-6.1-sol", "0.00262")]
    [DataRow("gpt-6-astra", "0.0132")]
    [DataRow("gpt-6-sol", "0.00264")]
    [DataRow("gpt-6-luna", "0.000132")]
    public async Task Initialize_CodexCatalogUpgradeFirstPricesHistoryAndKeepsFrozenRates(
        string model, string expectedAmount)
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        await writer.CommitAsync(Batch(Event(
            "event-upgrade-frozen", "dedup-upgrade-frozen", "gpt-5.6-sol",
            tokens: DeepSeekTokens())), CancellationToken.None);
        await writer.CommitAsync(Batch(Event(
            "event-upgrade-new", "dedup-upgrade-new", model,
            sourceRevision: 2, tokens: DeepSeekTokens())), CancellationToken.None);
        string rawModel = $"openrouter/openai/{model}";
        await using (SqliteConnection connection =
            await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand simulateOldVersion = connection.CreateCommand();
            simulateOldVersion.CommandText = """
                UPDATE usage_events
                SET price_catalog_version = 'frozen-prior-codex',
                    price_rule_id = 'openai/gpt-5.6-sol',
                    input_rate_usd_per_million = '5',
                    cached_input_rate_usd_per_million = '0.5',
                    cache_write_rate_usd_per_million = '6.25',
                    output_rate_usd_per_million = '30',
                    estimated_cost_usd = '0.0071'
                WHERE event_id = 'event-upgrade-frozen';
                UPDATE usage_events
                SET raw_model = $raw_model, normalized_model = $raw_model,
                    pricing_status = 0, price_catalog_version = NULL,
                    price_rule_id = NULL, estimated_cost_usd = NULL,
                    input_rate_usd_per_million = NULL,
                    cached_input_rate_usd_per_million = NULL,
                    cache_write_rate_usd_per_million = NULL,
                    output_rate_usd_per_million = NULL,
                    price_context_multiplier = NULL,
                    output_price_context_multiplier = NULL
                WHERE event_id = 'event-upgrade-new';
                UPDATE model_identity_catalog_state
                SET catalog_version = 'market-models-2026-08-10-r2';
                UPDATE pricing_catalog_state
                SET catalog_version = 'prior-offline-codex';
                """;
            simulateOldVersion.Parameters.AddWithValue("$raw_model", rawModel);
            await simulateOldVersion.ExecuteNonQueryAsync(CancellationToken.None);
        }
        StoredPrice frozen = await ReadPriceAsync(connections, "dedup-upgrade-frozen");
        await writer.InitializeAsync(CancellationToken.None);
        StoredPrice priced = await ReadPriceAsync(connections, "dedup-upgrade-new");
        Assert.AreEqual(EventPricingStatus.Complete, priced.Status);
        Assert.AreEqual(decimal.Parse(expectedAmount, CultureInfo.InvariantCulture),
            priced.Amount);
        Assert.AreEqual($"openai/{model}", priced.RuleId);
        Assert.AreEqual(OfflinePriceCatalog.CurrentVersion, priced.CatalogVersion);
        Assert.AreEqual(frozen,
            await ReadPriceAsync(connections, "dedup-upgrade-frozen"));
        await using (SqliteConnection verify =
            await connections.OpenReaderAsync(CancellationToken.None))
        {
            await using SqliteCommand identity = verify.CreateCommand();
            identity.CommandText = """
                SELECT raw_model, normalized_model,
                    (SELECT input_rate_usd_per_million FROM usage_events
                     WHERE event_id = 'event-upgrade-frozen'),
                    (SELECT COUNT(*) FROM usage_events)
                FROM usage_events WHERE event_id = 'event-upgrade-new';
                """;
            await using SqliteDataReader reader =
                await identity.ExecuteReaderAsync(CancellationToken.None);
            Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
            Assert.AreEqual(rawModel, reader.GetString(0));
            Assert.AreEqual(model, reader.GetString(1));
            Assert.AreEqual("5", reader.GetString(2));
            Assert.AreEqual(2L, reader.GetInt64(3));
        }
        await writer.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(priced,
            await ReadPriceAsync(connections, "dedup-upgrade-new"));
        Assert.AreEqual(frozen,
            await ReadPriceAsync(connections, "dedup-upgrade-frozen"));
    }

    [TestMethod]
    public async Task Commit_BindsBuiltInRateSnapshotAndNeverRepricesIt()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        await writer.CommitAsync(
            Batch(Event("event-built-in", "dedup-built-in", "gpt-5.3-codex")),
            CancellationToken.None);
        StoredPrice before =
            await ReadPriceAsync(connections, "dedup-built-in");
        var ledger = new SqlitePriceLedger(connections);

        int rebound = await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "gpt-5.3-codex",
                100m,
                10m,
                null,
                200m),
            CancellationToken.None);

        StoredPrice after =
            await ReadPriceAsync(connections, "dedup-built-in");
        Assert.AreEqual(EventPricingStatus.Complete, before.Status);
        Assert.AreEqual(OfflinePriceCatalog.CurrentVersion, before.CatalogVersion);
        Assert.AreEqual("openai/gpt-5.3-codex", before.RuleId);
        Assert.IsNotNull(before.Amount);
        Assert.IsGreaterThan(0m, before.Amount.Value);
        Assert.AreEqual(0, rebound);
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task Commit_BindsDeepSeekV4PublishedRates()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        UsageEvent flash = Event(
            "event-deepseek-flash",
            "dedup-deepseek-flash",
            "deepseek-v4-flash",
            tokens: DeepSeekTokens());
        UsageEvent pro = Event(
            "event-deepseek-pro",
            "dedup-deepseek-pro",
            "deepseek-v4-pro",
            sourceRevision: 2,
            tokens: DeepSeekTokens());

        await writer.CommitAsync(Batch(flash), CancellationToken.None);
        await writer.CommitAsync(Batch(pro), CancellationToken.None);

        StoredPrice flashPrice =
            await ReadPriceAsync(connections, "dedup-deepseek-flash");
        StoredPrice proPrice =
            await ReadPriceAsync(connections, "dedup-deepseek-pro");
        Assert.AreEqual(EventPricingStatus.Complete, flashPrice.Status);
        Assert.AreEqual(0.00014056m, flashPrice.Amount);
        Assert.AreEqual("deepseek/deepseek-v4-flash-cache-cut", flashPrice.RuleId);
        Assert.AreEqual(EventPricingStatus.Complete, proPrice.Status);
        Assert.AreEqual(0.000435725m, proPrice.Amount);
        Assert.AreEqual("deepseek/deepseek-v4-pro-cache-cut", proPrice.RuleId);
    }

    [TestMethod]
    public async Task Initialize_DeepSeekUpgradeUsesStoredCallTimeAndKeepsFrozenAndCustomPrices()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        foreach (string model in new[] { "deepseek-flash", "deepseek-v4-pro" })
        {
            var peak = new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero);
            await writer.CommitAsync(Batch(Event($"{model}-frozen", $"{model}-frozen",
                model, tokens: DeepSeekTokens(), occurredAtUtc: peak.AddDays(-1))), CancellationToken.None);
            await writer.CommitAsync(Batch(Event($"{model}-peak", $"{model}-peak",
                model, 2, DeepSeekTokens(), peak)), CancellationToken.None);
            await writer.CommitAsync(Batch(Event($"{model}-off", $"{model}-off",
                model, 3, DeepSeekTokens(), peak.AddHours(3))), CancellationToken.None);
            await using (SqliteConnection connection =
                await connections.OpenWriterAsync(CancellationToken.None))
            {
                await using SqliteCommand prior = connection.CreateCommand();
                prior.CommandText = """
                    UPDATE usage_events SET
                        price_catalog_version = 'prior-deepseek-catalog',
                        price_rule_id = 'prior-frozen-rule',
                        input_rate_usd_per_million = '0.435',
                        cached_input_rate_usd_per_million = '0.003625',
                        cache_write_rate_usd_per_million = '0.435',
                        output_rate_usd_per_million = '0.87',
                        estimated_cost_usd = '0.000435725'
                    WHERE dedup_key = $frozen;
                    UPDATE usage_events SET
                        pricing_status = 0, price_catalog_version = NULL,
                        price_rule_id = NULL, estimated_cost_usd = NULL,
                        input_rate_usd_per_million = NULL,
                        cached_input_rate_usd_per_million = NULL,
                        cache_write_rate_usd_per_million = NULL,
                        output_rate_usd_per_million = NULL,
                        price_context_multiplier = NULL,
                        output_price_context_multiplier = NULL
                    WHERE dedup_key IN ($peak, $off);
                    UPDATE pricing_catalog_state SET catalog_version = 'prior-deepseek';
                    """;
                prior.Parameters.AddWithValue("$frozen", $"{model}-frozen");
                prior.Parameters.AddWithValue("$peak", $"{model}-peak");
                prior.Parameters.AddWithValue("$off", $"{model}-off");
                await prior.ExecuteNonQueryAsync(CancellationToken.None);
            }

            StoredPrice frozen = await ReadPriceAsync(connections, $"{model}-frozen");
            await writer.InitializeAsync(CancellationToken.None);
            StoredPrice peakPrice = await ReadPriceAsync(connections, $"{model}-peak");
            StoredPrice offPrice = await ReadPriceAsync(connections, $"{model}-off");
            decimal offAmount = model == "deepseek-flash" ? 0.0001806m : 0.0007304m;
            Assert.AreEqual(offAmount * 2, peakPrice.Amount);
            Assert.AreEqual(offAmount, offPrice.Amount);
            Assert.AreEqual($"deepseek/{model}/peak", peakPrice.RuleId);
            Assert.AreEqual($"deepseek/{model}/off-peak", offPrice.RuleId);
            Assert.AreEqual(frozen, await ReadPriceAsync(connections, $"{model}-frozen"));

            await writer.CommitAsync(Batch(Event($"{model}-unknown-year", $"{model}-unknown-year",
                model, 4, DeepSeekTokens(),
                new DateTimeOffset(2027, 1, 4, 1, 0, 0, TimeSpan.Zero))), CancellationToken.None);
            Assert.AreEqual(EventPricingStatus.Unpriced,
                (await ReadPriceAsync(connections, $"{model}-unknown-year")).Status);
            var queries = new SqliteUsageQueryService(connections);
            UsageOverview overview = await queries.GetOverviewAsync(
                new UsageFilter(peak, peak.AddYears(1), normalizedModel: model),
                CancellationToken.None);
            Assert.IsTrue(overview.Pricing!.MissingCategories.HasFlag(
                PricingMissingCategory.PriceTimeSchedule));
            PriceSettingRow setting = (await queries.GetPriceSettingsAsync(CancellationToken.None))
                .Single(row => row.NormalizedModel == model);
            Assert.IsNotNull(setting.BuiltInTimeSchedule);
            Assert.AreEqual(PriceSettingSource.BuiltInDefault, setting.Source);

            var ledger = new SqlitePriceLedger(connections);
            Assert.AreEqual(1, await ledger.SetCustomPriceAsync(
                new ModelPriceRate(model, 9m, 9m, 0m, 9m), CancellationToken.None));
            StoredPrice custom = await ReadPriceAsync(connections, $"{model}-unknown-year");
            Assert.AreEqual("user-v1", custom.CatalogVersion);
            Assert.AreEqual(0.0099m, custom.Amount);
            await writer.CommitAsync(Batch(Event($"{model}-custom", $"{model}-custom",
                model, 5, DeepSeekTokens(), peak)), CancellationToken.None);
            Assert.AreEqual(custom.Amount,
                (await ReadPriceAsync(connections, $"{model}-custom")).Amount);
            Assert.AreEqual(0, await ledger.RestoreDefaultAsync(model, CancellationToken.None));
            await writer.CommitAsync(Batch(Event($"{model}-restored", $"{model}-restored",
                model, 6, DeepSeekTokens(), peak)), CancellationToken.None);
            StoredPrice restored = await ReadPriceAsync(connections, $"{model}-restored");
            Assert.AreEqual(peakPrice.Amount, restored.Amount);
            Assert.AreEqual(peakPrice.RuleId, restored.RuleId);

            await writer.InitializeAsync(CancellationToken.None);
            Assert.AreEqual(frozen, await ReadPriceAsync(connections, $"{model}-frozen"));
            Assert.AreEqual(peakPrice, await ReadPriceAsync(connections, $"{model}-peak"));
            Assert.AreEqual(offPrice, await ReadPriceAsync(connections, $"{model}-off"));
            Assert.AreEqual(custom, await ReadPriceAsync(connections, $"{model}-unknown-year"));
        }
    }

    [TestMethod]
    public async Task Commit_BindsReviewedKimiPriceAliasesAndKeepsHy3Unpriced()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);

        await writer.CommitAsync(
            Batch(Event(
                "event-kimi-k3-256k",
                "dedup-kimi-k3-256k",
                "kimi-k3-256k",
                tokens: DeepSeekTokens())),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-kimi-for-coding",
                "dedup-kimi-for-coding",
                "kimi-for-coding",
                sourceRevision: 2,
                tokens: DeepSeekTokens())),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-kimi-k2.6-agent",
                "dedup-kimi-k2.6-agent",
                "kimi-k2.6-agent",
                sourceRevision: 3,
                tokens: DeepSeekTokens())),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-kimi-k3-agent",
                "dedup-kimi-k3-agent",
                "kimi-k3-agent",
                sourceRevision: 4,
                tokens: DeepSeekTokens())),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-hy3",
                "dedup-hy3",
                "hy3",
                sourceRevision: 5,
                tokens: DeepSeekTokens())),
            CancellationToken.None);

        StoredPrice kimiK3 =
            await ReadPriceAsync(connections, "dedup-kimi-k3-256k");
        StoredPrice kimiForCoding =
            await ReadPriceAsync(connections, "dedup-kimi-for-coding");
        StoredPrice kimiK26Agent =
            await ReadPriceAsync(connections, "dedup-kimi-k2.6-agent");
        StoredPrice kimiK3Agent =
            await ReadPriceAsync(connections, "dedup-kimi-k3-agent");
        StoredPrice hy3 = await ReadPriceAsync(connections, "dedup-hy3");

        Assert.AreEqual(EventPricingStatus.Complete, kimiK3.Status);
        StringAssert.Contains(kimiK3.RuleId, "moonshotai/kimi-k3");
        Assert.AreEqual(EventPricingStatus.Complete, kimiForCoding.Status);
        StringAssert.Contains(
            kimiForCoding.RuleId,
            "moonshotai/kimi-k2.7-code");
        Assert.AreEqual(EventPricingStatus.Complete, kimiK26Agent.Status);
        StringAssert.Contains(kimiK26Agent.RuleId, "moonshotai/kimi-k2.6");
        Assert.AreEqual(EventPricingStatus.Complete, kimiK3Agent.Status);
        StringAssert.Contains(kimiK3Agent.RuleId, "moonshotai/kimi-k3");
        Assert.AreEqual(EventPricingStatus.Unpriced, hy3.Status);
        Assert.AreEqual(
            PricingMissingCategory.ModelRate,
            hy3.MissingCategories);
    }

    [TestMethod]
    public async Task CustomPrice_BindsPreviouslyUnknownModelAndPricesNewEvents()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        await writer.CommitAsync(
            Batch(Event("event-unknown", "dedup-unknown", "private-model")),
            CancellationToken.None);
        StoredPrice unpriced =
            await ReadPriceAsync(connections, "dedup-unknown");
        var ledger = new SqlitePriceLedger(connections);

        int priced = await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "private-model",
                2m,
                0.2m,
                null,
                8m),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-unknown-new",
                "dedup-unknown-new",
                "private-model",
                sourceRevision: 2)),
            CancellationToken.None);

        StoredPrice rebound =
            await ReadPriceAsync(connections, "dedup-unknown");
        StoredPrice newlyBound =
            await ReadPriceAsync(connections, "dedup-unknown-new");
        Assert.AreEqual(EventPricingStatus.Unpriced, unpriced.Status);
        Assert.IsNull(unpriced.Amount);
        Assert.AreEqual(
            PricingMissingCategory.ModelRate,
            unpriced.MissingCategories);
        Assert.AreEqual(1, priced);
        Assert.AreEqual(EventPricingStatus.Complete, rebound.Status);
        Assert.AreEqual("user-v1", rebound.CatalogVersion);
        Assert.AreEqual("user:private-model", rebound.RuleId);
        Assert.AreEqual(rebound.Amount, newlyBound.Amount);
        Assert.HasCount(
            1,
            await ledger.GetCustomPricesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CustomPrice_CanonicalizesQualifiedIdentityAtLedgerBoundary()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        await writer.CommitAsync(
            Batch(Event(
                "event-qualified-custom",
                "dedup-qualified-custom",
                "openai/gpt-oss-120b")),
            CancellationToken.None);
        var ledger = new SqlitePriceLedger(connections);

        int priced = await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "openai/gpt-oss-120b",
                2m,
                0.2m,
                null,
                8m),
            CancellationToken.None);

        StoredPrice result =
            await ReadPriceAsync(connections, "dedup-qualified-custom");
        IReadOnlyList<CustomPriceSetting> settings =
            await ledger.GetCustomPricesAsync(CancellationToken.None);
        Assert.HasCount(1, settings);
        CustomPriceSetting setting = settings[0];
        Assert.AreEqual(1, priced);
        Assert.AreEqual(EventPricingStatus.Complete, result.Status);
        Assert.AreEqual("user:gpt-oss-120b", result.RuleId);
        Assert.AreEqual("gpt-oss-120b", setting.Rate.NormalizedModel);
    }

    [TestMethod]
    public async Task RestoreDefault_LeavesFrozenCustomEventAndUsesBuiltInForFutureEvent()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        var ledger = new SqlitePriceLedger(connections);
        await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "gpt-5.3-codex",
                100m,
                10m,
                null,
                200m),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event("event-custom", "dedup-custom", "gpt-5.3-codex")),
            CancellationToken.None);
        StoredPrice custom = await ReadPriceAsync(connections, "dedup-custom");

        int rebound = await ledger.RestoreDefaultAsync(
            "gpt-5.3-codex",
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-default",
                "dedup-default",
                "gpt-5.3-codex",
                sourceRevision: 2)),
            CancellationToken.None);

        StoredPrice frozen = await ReadPriceAsync(connections, "dedup-custom");
        StoredPrice builtIn = await ReadPriceAsync(connections, "dedup-default");
        Assert.AreEqual(0, rebound);
        Assert.AreEqual("user-v1", custom.CatalogVersion);
        Assert.AreEqual(custom, frozen);
        Assert.AreEqual(
            OfflinePriceCatalog.CurrentVersion,
            builtIn.CatalogVersion);
        Assert.IsNotNull(custom.Amount);
        Assert.IsNotNull(builtIn.Amount);
        Assert.IsGreaterThan(builtIn.Amount.Value, custom.Amount.Value);
        Assert.IsEmpty(
            await ledger.GetCustomPricesAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Queries_KeepCompletePartialAndUnpricedCoverageAcrossAllSurfaces()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        UsageEvent complete = Event(
            "event-complete",
            "dedup-complete",
            "gpt-5.3-codex") with
        {
            SessionId = "root-session",
            TurnIdHash = new string('a', 64),
            ProjectId = "project-1",
            ProjectPath = @"D:\Synthetic"
        };
        UsageEvent partial = Event(
            "event-partial",
            "dedup-partial",
            "gpt-5.6-sol",
            sourceRevision: 2) with
        {
            SessionId = "root-session",
            TurnIdHash = new string('a', 64),
            ProjectId = "project-1",
            ProjectPath = @"D:\Synthetic"
        };
        UsageEvent unpriced = Event(
            "event-unpriced",
            "dedup-unpriced",
            "internal-model",
            sourceRevision: 3) with
        {
            SessionId = "root-session",
            TurnIdHash = new string('a', 64),
            ProjectId = "project-1",
            ProjectPath = @"D:\Synthetic"
        };
        await writer.CommitAsync(Batch(complete), CancellationToken.None);
        await writer.CommitAsync(Batch(partial), CancellationToken.None);
        await writer.CommitAsync(Batch(unpriced), CancellationToken.None);
        var queries = new SqliteUsageQueryService(connections);
        UsageFilter filter = AllDay();

        UsageOverview overview =
            await queries.GetOverviewAsync(filter, CancellationToken.None);
        UsageTrendPoint trend = Assert.ContainsSingle(
            await queries.GetTrendAsync(filter, CancellationToken.None));
        IReadOnlyList<ModelUsageRow> models =
            await queries.GetModelsAsync(filter, CancellationToken.None);
        ProjectUsageRow project = Assert.ContainsSingle(
            await queries.GetProjectsAsync(filter, CancellationToken.None));
        RootSessionSummaryRow root = Assert.ContainsSingle(
            (await queries.GetRootSessionsAsync(
                new RootSessionPageRequest(filter),
                CancellationToken.None)).Items);
        RootSessionDetail detail = (await queries.GetRootSessionDetailAsync(
            filter,
            root.Identity,
            CancellationToken.None))!;
        TurnUsagePage turns = await queries.GetTurnsAsync(
            filter,
            root.Identity,
            CancellationToken.None);
        IReadOnlyList<UsageRecordRow> records =
            await queries.GetRecentRecordsAsync(filter, CancellationToken.None);

        AssertMixed(overview.Pricing);
        AssertMixed(trend.Pricing);
        AssertMixed(project.Pricing);
        AssertMixed(root.Pricing);
        AssertMixed(Assert.ContainsSingle(detail.Contributions).Pricing);
        AssertMixed(Assert.ContainsSingle(turns.Turns).Pricing);
        Assert.AreEqual(
            PricingCoverageStatus.NoData,
            turns.Unattributed.Pricing?.Coverage);
        Assert.HasCount(3, models);
        Assert.AreEqual(
            PricingCoverageStatus.Complete,
            models.Single(row => row.Model == "gpt-5.3-codex").Pricing?.Coverage);
        Assert.AreEqual(
            PricingCoverageStatus.Partial,
            models.Single(row => row.Model == "gpt-5.6-sol").Pricing?.Coverage);
        Assert.AreEqual(
            PricingCoverageStatus.Unpriced,
            models.Single(row => row.Model == "internal-model").Pricing?.Coverage);
        Assert.HasCount(3, records);
        Assert.IsTrue(records.Any(
            row => row.Pricing?.Status == EventPricingStatus.Complete));
        Assert.IsTrue(records.Any(
            row => row.Pricing?.Status == EventPricingStatus.Partial));
        Assert.IsTrue(records.Any(
            row => row.Pricing?.Status == EventPricingStatus.Unpriced));
    }

    [TestMethod]
    public async Task PriceSettingsQuery_ReturnsObservedModelsAndRetainedOverridesOnly()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        await writer.CommitAsync(
            Batch(Event("event-default-row", "dedup-default-row", "gpt-5.3-codex")),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-private-row",
                "dedup-private-row",
                "private-model",
                sourceRevision: 2)),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-kimi-k3-256k-row",
                "dedup-kimi-k3-256k-row",
                "kimi-k3-256k",
                sourceRevision: 3)),
            CancellationToken.None);
        var ledger = new SqlitePriceLedger(connections);
        await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "private-model",
                2m,
                0.2m,
                null,
                8m,
                100_000,
                2m,
                1.5m),
            CancellationToken.None);
        await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "legacy-custom-model",
                3m,
                null,
                null,
                9m),
            CancellationToken.None);
        var queries = new SqliteUsageQueryService(connections);

        IReadOnlyList<PriceSettingRow> rows =
            await queries.GetPriceSettingsAsync(CancellationToken.None);

        PriceSettingRow builtIn = rows.Single(row =>
            row.NormalizedModel == "gpt-5.3-codex");
        PriceSettingRow custom = rows.Single(row =>
            row.NormalizedModel == "private-model");
        PriceSettingRow retained = rows.Single(row =>
            row.NormalizedModel == "legacy-custom-model");
        PriceSettingRow reviewedAlias = rows.Single(row =>
            row.NormalizedModel == "kimi-k3-256k");
        Assert.HasCount(4, rows);
        Assert.AreEqual(PriceSettingSource.BuiltInDefault, builtIn.Source);
        Assert.IsNotNull(builtIn.BuiltInRate);
        Assert.IsNull(builtIn.CustomRate);
        Assert.AreEqual(1L, builtIn.ObservedRecords);
        Assert.AreEqual(PriceSettingSource.CustomOverride, custom.Source);
        Assert.IsNull(custom.BuiltInRate);
        Assert.IsNotNull(custom.CustomRate);
        Assert.AreEqual(
            100_000L,
            custom.EffectiveRate?.LongContextThresholdTokens);
        Assert.AreEqual(1L, custom.ObservedRecords);
        Assert.AreEqual(0L, retained.ObservedRecords);
        Assert.IsNotNull(retained.CustomRate);
        Assert.AreEqual(3m, reviewedAlias.BuiltInRate?.InputUsdPerMillion);
        Assert.AreEqual(
            0.3m,
            reviewedAlias.BuiltInRate?.CachedInputUsdPerMillion);
        Assert.IsNull(reviewedAlias.BuiltInRate?.CacheWriteUsdPerMillion);
        Assert.AreEqual(15m, reviewedAlias.BuiltInRate?.OutputUsdPerMillion);
        Assert.IsFalse(rows.Any(row =>
            row.NormalizedModel == "claude-3-5-haiku-20241022"));
    }

    [TestMethod]
    public async Task PriceSettingsQuery_MissingDatabaseDoesNotCreateWriterState()
    {
        using var directory = new TestTempDirectory();
        string databasePath = directory.File("missing-price-settings.db");
        var queries = new SqliteUsageQueryService(
            new SqliteConnectionFactory(new StorageOptions(databasePath)));

        await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await queries.GetPriceSettingsAsync(CancellationToken.None));

        Assert.IsFalse(File.Exists(databasePath));
    }

    [TestMethod]
    public async Task RestoreAllDefaults_RemovesOverridesAndPreservesFrozenEvents()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) =
            await CreateAsync(directory);
        var ledger = new SqlitePriceLedger(connections);
        await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "gpt-5.3-codex",
                100m,
                10m,
                null,
                200m),
            CancellationToken.None);
        await ledger.SetCustomPriceAsync(
            new ModelPriceRate(
                "private-model",
                2m,
                null,
                null,
                8m),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event("event-custom-default", "dedup-custom-default", "gpt-5.3-codex")),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-custom-private",
                "dedup-custom-private",
                "private-model",
                sourceRevision: 2)),
            CancellationToken.None);

        int newlyPriced =
            await ledger.RestoreAllDefaultsAsync(CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-new-default",
                "dedup-new-default",
                "gpt-5.3-codex",
                sourceRevision: 3)),
            CancellationToken.None);
        await writer.CommitAsync(
            Batch(Event(
                "event-new-private",
                "dedup-new-private",
                "private-model",
                sourceRevision: 4)),
            CancellationToken.None);

        Assert.AreEqual(0, newlyPriced);
        Assert.IsEmpty(
            await ledger.GetCustomPricesAsync(CancellationToken.None));
        Assert.AreEqual(
            "user-v1",
            (await ReadPriceAsync(connections, "dedup-custom-default"))
                .CatalogVersion);
        Assert.AreEqual(
            "user-v1",
            (await ReadPriceAsync(connections, "dedup-custom-private"))
                .CatalogVersion);
        Assert.AreEqual(
            OfflinePriceCatalog.CurrentVersion,
            (await ReadPriceAsync(connections, "dedup-new-default"))
                .CatalogVersion);
        Assert.AreEqual(
            EventPricingStatus.Unpriced,
            (await ReadPriceAsync(connections, "dedup-new-private")).Status);
    }

    [TestMethod]
    public async Task EffectivePrices_UpgradeMigrationRetainsRatesAndTransfersAllPeriodsForLateImports()
    {
        using var directory = new TestTempDirectory();
        (SqliteConnectionFactory connections, SqliteUsageWriter writer) = await CreateAsync(directory);
        await writer.CommitAsync(Batch(Event("frozen", "frozen", "gpt-5.3-codex",
            tokens: DeepSeekTokens())), CancellationToken.None);
        StoredPrice frozen = await ReadPriceAsync(connections, "frozen");
        await using (SqliteConnection connection = await connections.OpenWriterAsync(CancellationToken.None))
        {
            await using SqliteCommand v14 = connection.CreateCommand();
            v14.CommandText = """
                DROP TABLE pricing_overrides;
                CREATE TABLE pricing_overrides (
                    normalized_model TEXT NOT NULL PRIMARY KEY,
                    input_rate_usd_per_million TEXT NOT NULL,
                    cached_input_rate_usd_per_million TEXT NULL,
                    cache_write_rate_usd_per_million TEXT NULL,
                    output_rate_usd_per_million TEXT NOT NULL,
                    long_context_threshold_tokens INTEGER NULL,
                    long_context_input_multiplier TEXT NOT NULL,
                    long_context_output_multiplier TEXT NOT NULL,
                    updated_at_unix_ms INTEGER NOT NULL);
                INSERT INTO pricing_overrides VALUES (
                    'gpt-5.3-codex', '2', '0.2', '0', '8', NULL, '1', '1', 1784156400000);
                DELETE FROM schema_migrations WHERE version=15;
                PRAGMA user_version=14;
                """;
            await v14.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await writer.InitializeAsync(CancellationToken.None);
        var ledger = new SqlitePriceLedger(connections);
        PriceOverridePeriod migrated = Assert.ContainsSingle(await ledger.GetOverridePeriodsAsync(CancellationToken.None));
        Assert.AreEqual(DateTimeOffset.MinValue, migrated.EffectiveFromUtc);
        Assert.AreEqual(2m, migrated.Rate?.InputUsdPerMillion);
        var change = new DateTimeOffset(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        await ledger.SetCustomPriceAsync(new ModelPriceRate("gpt-5.3-codex", 4m, 0.4m, 0m, 16m),
            change, CancellationToken.None);
        await ledger.RestoreDefaultAsync("gpt-5.3-codex", change.AddDays(1), CancellationToken.None);
        IReadOnlyList<PriceOverridePeriod> periods = await ledger.GetOverridePeriodsAsync(CancellationToken.None);
        Assert.HasCount(3, periods);
        Assert.IsNull(periods[^1].Rate);
        Assert.AreEqual(frozen, await ReadPriceAsync(connections, "frozen"));
        var queries = new SqliteUsageQueryService(connections);
        PriceSettingRow setting = Assert.ContainsSingle(await queries.GetPriceSettingsAsync(CancellationToken.None));
        Assert.AreEqual(PriceSettingSource.BuiltInDefault, setting.Source);
        Assert.HasCount(3, setting.CustomHistory!);

        using var stagedDirectory = new TestTempDirectory();
        (SqliteConnectionFactory stagedConnections, SqliteUsageWriter stagedWriter) = await CreateAsync(stagedDirectory);
        await new SqlitePriceLedger(stagedConnections).ReplaceOverridePeriodsAsync(periods, CancellationToken.None);
        (DateTimeOffset Time, decimal Amount)[] cases =
        [
            (change.AddMilliseconds(-1), 0.00244m),
            (change, 0.00488m),
            (change.AddDays(1), 0.002835m)
        ];
        foreach ((SqliteConnectionFactory db, SqliteUsageWriter target) in new[]
                 { (connections, writer), (stagedConnections, stagedWriter) })
        {
            for (int index = 0; index < cases.Length; index++)
            {
                string id = $"late-{index}";
                await target.CommitAsync(Batch(Event(id, id, "gpt-5.3-codex", index + 2,
                    DeepSeekTokens(), cases[index].Time)), CancellationToken.None);
                Assert.AreEqual(cases[index].Amount, (await ReadPriceAsync(db, id)).Amount);
            }
            await target.InitializeAsync(CancellationToken.None);
            Assert.AreEqual(0.00244m, (await ReadPriceAsync(db, "late-0")).Amount);
        }
        Assert.AreEqual(frozen, await ReadPriceAsync(connections, "frozen"));
    }

    [TestMethod]
    public async Task OfficialHistory_BackfillAndLateImportsUseOccurrenceTimeWithoutRepricingSnapshots()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(new StorageOptions(directory.File("official-history.db")));
        var previous = new OfflinePriceCatalog("previous-flat-price",
            [new("previous-flat-price", "previous-sol", "gpt-5.6-sol",
                new ModelPriceRate("gpt-5.6-sol", 4m, 0.4m, 5m, 20m))]);
        var oldWriter = new SqliteUsageWriter(connections, previous);
        await oldWriter.InitializeAsync(CancellationToken.None);
        var cut = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        await oldWriter.CommitAsync(Batch(Event("frozen", "frozen", "gpt-5.6-sol",
            tokens: DeepSeekTokens(), occurredAtUtc: cut.AddDays(-10))), CancellationToken.None);
        StoredPrice frozen = await ReadPriceAsync(connections, "frozen");
        var waiting = new SqliteUsageWriter(connections, new OfflinePriceCatalog("missing", []));
        await waiting.InitializeAsync(CancellationToken.None);
        await waiting.CommitAsync(Batch(Event("old", "old", "gpt-5.6-sol", 2,
            DeepSeekTokens(), cut.AddMilliseconds(-1))), CancellationToken.None);
        await waiting.CommitAsync(Batch(Event("new", "new", "gpt-5.6-sol", 3,
            DeepSeekTokens(), cut)), CancellationToken.None);
        var upgraded = new SqliteUsageWriter(connections);
        await upgraded.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(0.0071m, (await ReadPriceAsync(connections, "old")).Amount);
        Assert.AreEqual(0.00528m, (await ReadPriceAsync(connections, "new")).Amount);
        await upgraded.CommitAsync(Batch(Event("late", "late", "gpt-5.6-sol", 4,
            DeepSeekTokens(), DateTimeOffset.UnixEpoch)), CancellationToken.None);
        Assert.AreEqual(0.0071m, (await ReadPriceAsync(connections, "late")).Amount);
        Assert.AreEqual(frozen, await ReadPriceAsync(connections, "frozen"));
    }

    [TestMethod]
    public async Task EffectivePrices_BackfillUsesOldAndNewCatalogPeriodsAndFreezesExistingRows()
    {
        using var directory = new TestTempDirectory();
        var connections = new SqliteConnectionFactory(new StorageOptions(directory.File("dated-catalog.db")));
        var writer = new SqliteUsageWriter(connections, new OfflinePriceCatalog("unpriced", []));
        await writer.InitializeAsync(CancellationToken.None);
        var change = new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero);
        await writer.CommitAsync(Batch(Event("old", "old", "dated-model", tokens: DeepSeekTokens(),
            occurredAtUtc: change.AddMilliseconds(-1))), CancellationToken.None);
        await writer.CommitAsync(Batch(Event("new", "new", "dated-model", 2, DeepSeekTokens(), change)), CancellationToken.None);
        var catalog = new OfflinePriceCatalog("dated-catalog-v1",
        [
            new("dated-catalog-v1", "old-price", "dated-model",
                new ModelPriceRate("dated-model", 1m, 0m, 0m, 10m), EffectiveUntilUtc: change),
            new("dated-catalog-v1", "new-price", "dated-model",
                new ModelPriceRate("dated-model", 2m, 0m, 0m, 20m), EffectiveFromUtc: change)
        ]);
        var datedWriter = new SqliteUsageWriter(connections, catalog);
        await datedWriter.InitializeAsync(CancellationToken.None);
        StoredPrice old = await ReadPriceAsync(connections, "old");
        StoredPrice current = await ReadPriceAsync(connections, "new");
        Assert.AreEqual(0.0018m, old.Amount);
        Assert.AreEqual("old-price", old.RuleId);
        Assert.AreEqual(0.0036m, current.Amount);
        Assert.AreEqual("new-price", current.RuleId);
        await datedWriter.CommitAsync(Batch(Event("late-old", "late-old", "dated-model", 3,
            DeepSeekTokens(), change.AddDays(-2))), CancellationToken.None);
        Assert.AreEqual(old.Amount, (await ReadPriceAsync(connections, "late-old")).Amount);
        await new SqlitePriceLedger(connections, catalog).SetCustomPriceAsync(
            new ModelPriceRate("dated-model", 9m, 9m, 0m, 9m), change, CancellationToken.None);
        await datedWriter.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(old, await ReadPriceAsync(connections, "old"));
        Assert.AreEqual(current, await ReadPriceAsync(connections, "new"));
    }

    private static async Task<(SqliteConnectionFactory, SqliteUsageWriter)>
        CreateAsync(TestTempDirectory directory)
    {
        var connections = new SqliteConnectionFactory(
            new StorageOptions(directory.File("pricing.db")));
        var writer = new SqliteUsageWriter(connections);
        await writer.InitializeAsync(CancellationToken.None);
        return (connections, writer);
    }

    private static UsageEventBatch Batch(UsageEvent usageEvent) => new(
        new SourceInstanceDescriptor(
            "codex:windows:test",
            "codex",
            SourceKind.Jsonl,
            "Codex test",
            @"C:\codex"),
        new SourceEntityDescriptor(
            "codex:windows:test",
            "rollout:test",
            @"C:\codex\rollout.jsonl"),
        $"cursor-{usageEvent.SourceRevision}",
        usageEvent.SourceFingerprint,
        usageEvent.ParserVersion,
        new DateTimeOffset(2026, 7, 16, 0, 1, 0, TimeSpan.Zero),
        [usageEvent]);

    private static UsageEvent Event(
        string eventId,
        string dedupKey,
        string model,
        long sourceRevision = 1,
        TokenUsage? tokens = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        DateTimeOffset timestamp =
            new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);
        return new UsageEvent(
            "codex",
            "codex:windows:test",
            "rollout:test",
            eventId,
            dedupKey,
            SourceKind.Jsonl,
            occurredAtUtc ?? timestamp,
            timestamp,
            new ModelIdentity
            {
                RawModel = model,
                NormalizedModel = model,
                ProviderId = "openai",
                ResolutionOrigin = ModelResolutionOrigin.LogConfirmed
            },
            tokens ?? new TokenUsage
            {
                InputReported = TokenMetric.Exact(1_000),
                UncachedInput = TokenMetric.Exact(800),
                CacheRead = TokenMetric.Exact(200),
                CacheWrite = TokenMetric.Unavailable,
                Output = TokenMetric.Exact(100),
                CacheIncludedInInput = MetricInclusion.Included,
                ReasoningIncludedInOutput = MetricInclusion.Included
            },
            CompletionState.Completed,
            DataQuality.Exact,
            "pricing-fixture-v1",
            "pricing-fixture",
            sourceRevision);
    }

    private static async Task<StoredPrice> ReadPriceAsync(
        SqliteConnectionFactory connections,
        string dedupKey)
    {
        await using SqliteConnection connection =
            await connections.OpenReaderAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
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
            WHERE dedup_key = $dedup_key;
            """;
        command.Parameters.AddWithValue("$dedup_key", dedupKey);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.IsTrue(await reader.ReadAsync(CancellationToken.None));
        return new StoredPrice(
            (EventPricingStatus)reader.GetInt32(0),
            reader.IsDBNull(1)
                ? null
                : decimal.Parse(
                    reader.GetString(1),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture),
            (PricingMissingCategory)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static UsageFilter AllDay() => new(
        new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero));

    private static TokenUsage DeepSeekTokens() => new()
    {
        InputReported = TokenMetric.Exact(1_000),
        UncachedInput = TokenMetric.Exact(800),
        CacheRead = TokenMetric.Exact(200),
        CacheWrite = TokenMetric.Exact(0),
        Output = TokenMetric.Exact(100),
        CacheIncludedInInput = MetricInclusion.Included,
        ReasoningIncludedInOutput = MetricInclusion.Included
    };

    private static void AssertMixed(PricingAggregate? pricing)
    {
        Assert.IsNotNull(pricing);
        Assert.AreEqual(PricingCoverageStatus.Partial, pricing.Coverage);
        Assert.AreEqual(1, pricing.CompleteRecords);
        Assert.AreEqual(1, pricing.PartialRecords);
        Assert.AreEqual(1, pricing.UnpricedRecords);
        Assert.IsNotNull(pricing.KnownAmountUsd);
        Assert.IsTrue(
            pricing.MissingCategories.HasFlag(
                PricingMissingCategory.CacheWriteTokens));
        Assert.IsTrue(
            pricing.MissingCategories.HasFlag(
                PricingMissingCategory.ModelRate));
    }

    private sealed record StoredPrice(
        EventPricingStatus Status,
        decimal? Amount,
        PricingMissingCategory MissingCategories,
        string? CatalogVersion,
        string? RuleId,
        string? InputRate,
        string? CachedInputRate,
        string? CacheWriteRate,
        string? OutputRate,
        string? InputMultiplier,
        string? OutputMultiplier);
}
