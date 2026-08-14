using System.Collections.Frozen;
using System.Text.Json;

namespace AgenTally.Storage.Pricing;

internal static class UpstreamPriceCatalogLoader
{
    private const string ResourceName = "AgenTally.UpstreamTokenPrices.json";

    public static UpstreamPriceCatalogData Load()
    {
        using Stream stream =
            typeof(UpstreamPriceCatalogLoader).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException(
                $"Embedded upstream price catalog '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<UpstreamPriceCatalogDocument>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(
                "Embedded upstream price catalog did not contain a document.");
        ValidateDocument(document);

        var sourceIds = document.DataSources
            .Select(static source => source.Id)
            .ToFrozenSet(StringComparer.Ordinal);
        var rules = new Dictionary<string, ResolvedPriceRule>(
            StringComparer.Ordinal);
        var ruleCounts = document.DataSources.ToDictionary(
            static source => source.Id,
            static _ => 0,
            StringComparer.Ordinal);
        foreach (UpstreamPriceCatalogRule sourceRule in document.Rules)
        {
            ValidateSourceRule(sourceRule, sourceIds);
            ruleCounts[sourceRule.Source]++;
            foreach (string sourceModel in sourceRule.Models)
            {
                string model = ModelPriceRate.NormalizeModel(sourceModel);
                if (!string.Equals(
                        model,
                        sourceModel,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Upstream price model '{sourceModel}' is not normalized.");
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

        foreach (UpstreamPriceDataSource source in document.DataSources)
        {
            if (source.SelectedRuleCount != ruleCounts[source.Id])
            {
                throw new InvalidDataException(
                    $"Upstream price source '{source.Id}' count does not match its rules.");
            }
        }

        return new UpstreamPriceCatalogData(
            document.CatalogVersion,
            rules.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void ValidateDocument(UpstreamPriceCatalogDocument document)
    {
        if (document.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(document.CatalogVersion) ||
            !string.Equals(document.Currency, "USD", StringComparison.Ordinal) ||
            !string.Equals(
                document.Unit,
                "per_million_tokens",
                StringComparison.Ordinal) ||
            !string.Equals(
                document.SelectionPolicy,
                "official-provider-modelsdev-then-direct-litellm-v1",
                StringComparison.Ordinal) ||
            document.DataSources is null ||
            document.DataSources.Count != 2 ||
            document.ShadowedByMaintainedCount < 0 ||
            document.SourceDisagreementCount < 0 ||
            document.Rules is null ||
            document.Rules.Count == 0)
        {
            throw new InvalidDataException(
                "Embedded upstream price catalog metadata is invalid.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (UpstreamPriceDataSource source in document.DataSources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) ||
                string.IsNullOrWhiteSpace(source.Artifact) ||
                !Uri.TryCreate(source.Uri, UriKind.Absolute, out Uri? uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                source.Sha256.Length != 64 ||
                source.Sha256.Any(static value => !Uri.IsHexDigit(value)) ||
                source.SelectedRuleCount < 0 ||
                !sourceIds.Add(source.Id))
            {
                throw new InvalidDataException(
                    "Embedded upstream price catalog contains an invalid source.");
            }
        }

        if (!sourceIds.SetEquals(["models.dev", "litellm"]))
        {
            throw new InvalidDataException(
                "Embedded upstream price catalog source set is invalid.");
        }
    }

    private static void ValidateSourceRule(
        UpstreamPriceCatalogRule rule,
        IReadOnlySet<string> sourceIds)
    {
        if (string.IsNullOrWhiteSpace(rule.RuleId) ||
            string.IsNullOrWhiteSpace(rule.Source) ||
            !sourceIds.Contains(rule.Source) ||
            string.IsNullOrWhiteSpace(rule.SourceKey) ||
            !rule.RuleId.StartsWith(
                $"{rule.Source}:",
                StringComparison.Ordinal) ||
            rule.Models is null ||
            rule.Models.Count == 0)
        {
            throw new InvalidDataException(
                "Embedded upstream price catalog contains an invalid rule.");
        }
    }

    private sealed record UpstreamPriceCatalogDocument(
        int SchemaVersion,
        string CatalogVersion,
        string Currency,
        string Unit,
        string SelectionPolicy,
        IReadOnlyList<UpstreamPriceDataSource> DataSources,
        int ShadowedByMaintainedCount,
        int SourceDisagreementCount,
        IReadOnlyList<UpstreamPriceCatalogRule> Rules);

    private sealed record UpstreamPriceDataSource(
        string Id,
        string Uri,
        string Artifact,
        string Sha256,
        int SelectedRuleCount);

    private sealed record UpstreamPriceCatalogRule(
        string RuleId,
        string Source,
        string SourceKey,
        IReadOnlyList<string> Models,
        decimal InputUsdPerMillion,
        decimal? CachedInputUsdPerMillion,
        decimal? CacheWriteUsdPerMillion,
        decimal OutputUsdPerMillion,
        long? LongContextThresholdTokens = null,
        decimal LongContextInputMultiplier = 1m,
        decimal LongContextOutputMultiplier = 1m);
}

internal sealed record UpstreamPriceCatalogData(
    string Version,
    FrozenDictionary<string, ResolvedPriceRule> Rules);
