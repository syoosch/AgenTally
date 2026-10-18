using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace AgenTally.Storage.Pricing;

internal static class OfficialApiPriceCatalogLoader
{
    private const string ResourceName = "AgenTally.OfficialApiTokenPrices.json";

    public static OfficialApiPriceCatalogData Load()
    {
        using Stream stream =
            typeof(OfficialApiPriceCatalogLoader).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException(
                $"Embedded official API price catalog '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<PriceCatalogDocument>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(
                "Embedded official API price catalog did not contain a document.");
        if (document.SchemaVersion is not (1 or 2 or 3) ||
            string.IsNullOrWhiteSpace(document.CatalogVersion) ||
            !string.Equals(document.Currency, "USD", StringComparison.Ordinal) ||
            !string.Equals(
                document.Unit,
                "per_million_tokens",
                StringComparison.Ordinal) ||
            document.Rules is null ||
            document.Rules.Count == 0)
        {
            throw new InvalidDataException(
                "Embedded official API price catalog metadata is invalid.");
        }

        var rules = new List<ResolvedPriceRule>();
        foreach (PriceCatalogRule sourceRule in document.Rules)
        {
            ValidateSourceRule(sourceRule);
            if (sourceRule.PeakOffPeak is not null && document.SchemaVersion < 2)
            {
                throw new InvalidDataException(
                    "Time-scheduled prices require catalog schema v2.");
            }

            PeakOffPeakSchedule? schedule = sourceRule.PeakOffPeak is null
                ? null : new PeakOffPeakSchedule(sourceRule.PeakOffPeak);
            DateTimeOffset? from = PriceRuleTimeline.ParseBoundary(sourceRule.EffectiveFromUtc);
            DateTimeOffset? until = PriceRuleTimeline.ParseBoundary(sourceRule.EffectiveUntilUtc);
            if ((from.HasValue || until.HasValue) &&
                (document.SchemaVersion < 3 || !Uri.TryCreate(sourceRule.EffectiveDateSource,
                    UriKind.Absolute, out Uri? evidence) || evidence.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException("Dated official prices require schema v3 and date evidence.");
            }
            foreach (string sourceModel in sourceRule.Models)
            {
                string model = ModelPriceRate.NormalizeModel(sourceModel);
                if (!string.Equals(
                        model,
                        sourceModel,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Official API price model '{sourceModel}' is not normalized.");
                }

                var rate = new ModelPriceRate(
                    model,
                    sourceRule.InputUsdPerMillion,
                    sourceRule.CachedInputUsdPerMillion,
                    sourceRule.CacheWriteUsdPerMillion,
                    sourceRule.OutputUsdPerMillion,
                    sourceRule.LongContextThresholdTokens,
                    sourceRule.LongContextInputMultiplier,
                    sourceRule.LongContextOutputMultiplier);
                if (schedule is not null &&
                    (rate.InputUsdPerMillion * schedule.PeakMultiplier > 1_000_000m ||
                     rate.CachedInputUsdPerMillion * schedule.PeakMultiplier > 1_000_000m ||
                     rate.CacheWriteUsdPerMillion * schedule.PeakMultiplier > 1_000_000m ||
                     rate.OutputUsdPerMillion * schedule.PeakMultiplier > 1_000_000m))
                {
                    throw new InvalidDataException("Peak price exceeds the supported limit.");
                }
                rules.Add(
                    new ResolvedPriceRule(
                        document.CatalogVersion,
                        sourceRule.RuleId,
                        model,
                        rate,
                        schedule,
                        from,
                        until,
                        sourceRule.EffectiveDateSource,
                        sourceRule.EffectiveFromBasis,
                        sourceRule.EffectiveUntilBasis));
            }
        }

        return new OfficialApiPriceCatalogData(
            document.CatalogVersion,
            PriceRuleTimeline.Build(rules));
    }

    private static void ValidateSourceRule(PriceCatalogRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId) ||
            string.IsNullOrWhiteSpace(rule.Provider) ||
            rule.Models is null ||
            rule.Models.Count == 0 ||
            !Uri.TryCreate(
                rule.OfficialSource,
                UriKind.Absolute,
                out Uri? sourceUri) ||
            !string.Equals(
                sourceUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !DateOnly.TryParseExact(
                rule.VerifiedOn,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            throw new InvalidDataException(
                "Official API price catalog contains an invalid source rule.");
        }
    }

    private sealed record PriceCatalogDocument(
        int SchemaVersion,
        string CatalogVersion,
        string Currency,
        string Unit,
        IReadOnlyList<PriceCatalogRule> Rules);

    private sealed record PriceCatalogRule(
        string RuleId,
        string Provider,
        string OfficialSource,
        string VerifiedOn,
        IReadOnlyList<string> Models,
        decimal InputUsdPerMillion,
        decimal? CachedInputUsdPerMillion,
        decimal? CacheWriteUsdPerMillion,
        decimal OutputUsdPerMillion,
        long? LongContextThresholdTokens = null,
        decimal LongContextInputMultiplier = 1m,
        decimal LongContextOutputMultiplier = 1m,
        PeakOffPeakScheduleDocument? PeakOffPeak = null,
        string? EffectiveFromUtc = null,
        string? EffectiveUntilUtc = null,
        string? EffectiveDateSource = null,
        string? EffectiveFromBasis = null,
        string? EffectiveUntilBasis = null);
}

internal sealed record OfficialApiPriceCatalogData(
    string Version,
    FrozenDictionary<string, IReadOnlyList<ResolvedPriceRule>> Rules);
