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
        if (document.SchemaVersion != 1 ||
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

        var rules = new Dictionary<string, ResolvedPriceRule>(
            StringComparer.Ordinal);
        foreach (PriceCatalogRule sourceRule in document.Rules)
        {
            ValidateSourceRule(sourceRule);
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
                rules.Add(
                    model,
                    new ResolvedPriceRule(
                        document.CatalogVersion,
                        sourceRule.RuleId,
                        model,
                        rate));
            }
        }

        return new OfficialApiPriceCatalogData(
            document.CatalogVersion,
            rules.ToFrozenDictionary(StringComparer.Ordinal));
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
        decimal LongContextOutputMultiplier = 1m);
}

internal sealed record OfficialApiPriceCatalogData(
    string Version,
    FrozenDictionary<string, ResolvedPriceRule> Rules);
