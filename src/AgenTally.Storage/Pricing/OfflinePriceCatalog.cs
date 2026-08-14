using System.Collections.Frozen;

namespace AgenTally.Storage.Pricing;

public sealed class OfflinePriceCatalog
{
    private static readonly Lazy<OfflinePriceCatalogData> Data = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static OfflinePriceCatalog Default { get; } = new();

    public static string CurrentVersion => Data.Value.Version;

    public string Version => CurrentVersion;

    public IReadOnlyList<ResolvedPriceRule> Entries =>
        Data.Value.Rules.Values
            .OrderBy(static value => value.MatchedModel, StringComparer.Ordinal)
            .ToArray();

    public bool TryResolve(
        string? normalizedModel,
        out ResolvedPriceRule? rule)
    {
        rule = null;
        return !string.IsNullOrWhiteSpace(normalizedModel) &&
               Data.Value.Rules.TryGetValue(
                   ModelPriceRate.NormalizeModel(normalizedModel),
                   out rule);
    }

    private static OfflinePriceCatalogData Load()
    {
        OfficialApiPriceCatalogData maintained =
            OfficialApiPriceCatalogLoader.Load();
        UpstreamPriceCatalogData upstream =
            UpstreamPriceCatalogLoader.Load();
        ReviewedPriceAliasCatalogData reviewedAliases =
            ReviewedPriceAliasCatalog.Load();
        string version =
            $"{maintained.Version}__{upstream.Version}__" +
            reviewedAliases.Version;
        var rules = new Dictionary<string, ResolvedPriceRule>(
            StringComparer.Ordinal);

        AddRules(rules, maintained.Rules.Values, version, overwrite: true);
        AddRules(rules, upstream.Rules.Values, version, overwrite: false);
        AddReviewedAliases(rules, reviewedAliases.Aliases, version);
        return new OfflinePriceCatalogData(
            version,
            rules.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void AddReviewedAliases(
        IDictionary<string, ResolvedPriceRule> rules,
        IReadOnlyDictionary<string, string> aliases,
        string catalogVersion)
    {
        foreach ((string alias, string pricedAs) in aliases)
        {
            if (!rules.TryGetValue(pricedAs, out ResolvedPriceRule? target))
            {
                throw new InvalidDataException(
                    $"Reviewed price alias '{alias}' targets missing rule " +
                    $"'{pricedAs}'.");
            }

            if (rules.ContainsKey(alias))
            {
                continue;
            }

            ModelPriceRate targetRate = target.Rate;
            var aliasRate = new ModelPriceRate(
                alias,
                targetRate.InputUsdPerMillion,
                targetRate.CachedInputUsdPerMillion,
                targetRate.CacheWriteUsdPerMillion,
                targetRate.OutputUsdPerMillion,
                targetRate.LongContextThresholdTokens,
                targetRate.LongContextInputMultiplier,
                targetRate.LongContextOutputMultiplier);
            rules.Add(
                alias,
                new ResolvedPriceRule(
                    catalogVersion,
                    $"reviewed-price-alias:{alias}->{pricedAs}|{target.RuleId}",
                    alias,
                    aliasRate));
        }
    }

    private static void AddRules(
        IDictionary<string, ResolvedPriceRule> destination,
        IEnumerable<ResolvedPriceRule> source,
        string catalogVersion,
        bool overwrite)
    {
        foreach (ResolvedPriceRule sourceRule in source)
        {
            ResolvedPriceRule rule = sourceRule with
            {
                CatalogVersion = catalogVersion
            };
            if (overwrite)
            {
                destination[rule.MatchedModel] = rule;
            }
            else
            {
                destination.TryAdd(rule.MatchedModel, rule);
            }
        }
    }
}

public sealed record ResolvedPriceRule(
    string CatalogVersion,
    string RuleId,
    string MatchedModel,
    ModelPriceRate Rate);

internal sealed record OfflinePriceCatalogData(
    string Version,
    FrozenDictionary<string, ResolvedPriceRule> Rules);
