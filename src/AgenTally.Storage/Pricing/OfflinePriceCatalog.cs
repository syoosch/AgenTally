using System.Collections.Frozen;

namespace AgenTally.Storage.Pricing;

public sealed class OfflinePriceCatalog
{
    private static readonly Lazy<OfflinePriceCatalogData> Data = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static OfflinePriceCatalog Default { get; } = new();

    private readonly OfflinePriceCatalogData? _customData;

    public OfflinePriceCatalog() { }

    public OfflinePriceCatalog(string version, IEnumerable<ResolvedPriceRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(rules);
        _customData = new OfflinePriceCatalogData(version, PriceRuleTimeline.Build(
            rules.Select(rule => rule with { CatalogVersion = version })));
    }

    private OfflinePriceCatalogData CatalogData => _customData ?? Data.Value;

    public static string CurrentVersion => Data.Value.Version;

    public string Version => CatalogData.Version;

    public IReadOnlyList<ResolvedPriceRule> Entries =>
        CatalogData.Rules.Values.SelectMany(static periods => periods)
            .OrderBy(static value => value.MatchedModel, StringComparer.Ordinal)
            .ToArray();

    public bool TryResolve(
        string? normalizedModel,
        out ResolvedPriceRule? rule) => TryResolve(normalizedModel, DateTimeOffset.UtcNow, out rule);

    public bool TryResolve(
        string? normalizedModel,
        DateTimeOffset occurredAtUtc,
        out ResolvedPriceRule? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(normalizedModel) ||
            !CatalogData.Rules.TryGetValue(ModelPriceRate.NormalizeModel(normalizedModel),
                out IReadOnlyList<ResolvedPriceRule>? periods))
        {
            return false;
        }
        rule = periods.FirstOrDefault(period => PriceRuleTimeline.Contains(period, occurredAtUtc));
        return rule is not null;
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
        var rules = new Dictionary<string, IReadOnlyList<ResolvedPriceRule>>(
            StringComparer.Ordinal);

        AddRules(rules, maintained.Rules.Values, version, overwrite: true);
        AddRules(rules, upstream.Rules.Values, version, overwrite: false);
        AddReviewedAliases(rules, reviewedAliases.Aliases, version);
        return new OfflinePriceCatalogData(
            version,
            rules.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void AddReviewedAliases(
        IDictionary<string, IReadOnlyList<ResolvedPriceRule>> rules,
        IReadOnlyDictionary<string, string> aliases,
        string catalogVersion)
    {
        foreach ((string alias, string pricedAs) in aliases)
        {
            if (!rules.TryGetValue(pricedAs, out IReadOnlyList<ResolvedPriceRule>? targets))
            {
                throw new InvalidDataException(
                    $"Reviewed price alias '{alias}' targets missing rule " +
                    $"'{pricedAs}'.");
            }

            if (rules.ContainsKey(alias))
            {
                continue;
            }

            var aliasPeriods = new List<ResolvedPriceRule>();
            foreach (ResolvedPriceRule target in targets)
            {
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
                aliasPeriods.Add(new ResolvedPriceRule(
                    catalogVersion,
                    $"reviewed-price-alias:{alias}->{pricedAs}|{target.RuleId}",
                    alias,
                    aliasRate,
                    target.PeakOffPeak,
                    target.EffectiveFromUtc,
                    target.EffectiveUntilUtc,
                    target.EffectiveDateSource,
                    target.EffectiveFromBasis,
                    target.EffectiveUntilBasis));
            }
            rules.Add(alias, aliasPeriods.AsReadOnly());
        }
    }

    private static void AddRules(
        IDictionary<string, IReadOnlyList<ResolvedPriceRule>> destination,
        IEnumerable<IReadOnlyList<ResolvedPriceRule>> source,
        string catalogVersion,
        bool overwrite)
    {
        foreach (IReadOnlyList<ResolvedPriceRule> sourcePeriods in source)
        {
            ResolvedPriceRule[] periods = sourcePeriods.Select(rule => rule with
                { CatalogVersion = catalogVersion }).ToArray();
            string model = periods[0].MatchedModel;
            if (overwrite)
            {
                destination[model] = Array.AsReadOnly(periods);
            }
            else
            {
                destination.TryAdd(model, Array.AsReadOnly(periods));
            }
        }
    }
}

public sealed record ResolvedPriceRule(
    string CatalogVersion,
    string RuleId,
    string MatchedModel,
    ModelPriceRate Rate,
    PeakOffPeakSchedule? PeakOffPeak = null,
    DateTimeOffset? EffectiveFromUtc = null,
    DateTimeOffset? EffectiveUntilUtc = null,
    string? EffectiveDateSource = null,
    string? EffectiveFromBasis = null,
    string? EffectiveUntilBasis = null);

internal sealed record OfflinePriceCatalogData(
    string Version,
    FrozenDictionary<string, IReadOnlyList<ResolvedPriceRule>> Rules);
