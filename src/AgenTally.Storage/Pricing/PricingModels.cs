namespace AgenTally.Storage.Pricing;

public enum EventPricingStatus
{
    Unpriced = 0,
    Complete = 1,
    Partial = 2
}

[Flags]
public enum PricingMissingCategory
{
    None = 0,
    ModelRate = 1 << 0,
    UncachedInputTokens = 1 << 1,
    CachedInputTokens = 1 << 2,
    CachedInputRate = 1 << 3,
    CacheWriteTokens = 1 << 4,
    CacheWriteRate = 1 << 5,
    OutputTokens = 1 << 6,
    LongContextInputTokens = 1 << 7
}

public enum PricingCoverageStatus
{
    NoData = 0,
    Complete = 1,
    Partial = 2,
    Unpriced = 3
}

public sealed record ModelPriceRate
{
    public ModelPriceRate(
        string normalizedModel,
        decimal inputUsdPerMillion,
        decimal? cachedInputUsdPerMillion,
        decimal? cacheWriteUsdPerMillion,
        decimal outputUsdPerMillion,
        long? longContextThresholdTokens = null,
        decimal longContextInputMultiplier = 1m,
        decimal longContextOutputMultiplier = 1m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedModel);
        if (normalizedModel.Length > 128 ||
            normalizedModel.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Normalized model is invalid.",
                nameof(normalizedModel));
        }

        ValidateRate(inputUsdPerMillion, nameof(inputUsdPerMillion));
        ValidateOptionalRate(
            cachedInputUsdPerMillion,
            nameof(cachedInputUsdPerMillion));
        ValidateOptionalRate(
            cacheWriteUsdPerMillion,
            nameof(cacheWriteUsdPerMillion));
        ValidateRate(outputUsdPerMillion, nameof(outputUsdPerMillion));
        if (longContextThresholdTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(longContextThresholdTokens));
        }

        ValidateMultiplier(
            longContextInputMultiplier,
            nameof(longContextInputMultiplier));
        ValidateMultiplier(
            longContextOutputMultiplier,
            nameof(longContextOutputMultiplier));

        NormalizedModel = NormalizeModel(normalizedModel);
        InputUsdPerMillion = inputUsdPerMillion;
        CachedInputUsdPerMillion = cachedInputUsdPerMillion;
        CacheWriteUsdPerMillion = cacheWriteUsdPerMillion;
        OutputUsdPerMillion = outputUsdPerMillion;
        LongContextThresholdTokens = longContextThresholdTokens;
        LongContextInputMultiplier = longContextInputMultiplier;
        LongContextOutputMultiplier = longContextOutputMultiplier;
    }

    public string NormalizedModel { get; }

    public decimal InputUsdPerMillion { get; }

    public decimal? CachedInputUsdPerMillion { get; }

    public decimal? CacheWriteUsdPerMillion { get; }

    public decimal OutputUsdPerMillion { get; }

    public long? LongContextThresholdTokens { get; }

    public decimal LongContextInputMultiplier { get; }

    public decimal LongContextOutputMultiplier { get; }

    internal static string NormalizeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model.Trim().ToLowerInvariant();
    }

    private static void ValidateOptionalRate(decimal? value, string name)
    {
        if (value.HasValue)
        {
            ValidateRate(value.Value, name);
        }
    }

    private static void ValidateRate(decimal value, string name)
    {
        if (value is < 0m or > 1_000_000m)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void ValidateMultiplier(decimal value, string name)
    {
        if (value is < 1m or > 100m)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }
}

public sealed record EventPriceEstimate(
    EventPricingStatus Status,
    decimal? KnownAmountUsd,
    PricingMissingCategory MissingCategories,
    string? CatalogVersion,
    string? RuleId,
    decimal? InputUsdPerMillion,
    decimal? CachedInputUsdPerMillion,
    decimal? CacheWriteUsdPerMillion,
    decimal? OutputUsdPerMillion,
    decimal? InputContextMultiplier,
    decimal? OutputContextMultiplier);

public sealed record PricingAggregate(
    decimal? KnownAmountUsd,
    int CompleteRecords,
    int PartialRecords,
    int UnpricedRecords,
    PricingMissingCategory MissingCategories)
{
    public int TotalRecords => CompleteRecords + PartialRecords + UnpricedRecords;

    public PricingCoverageStatus Coverage =>
        TotalRecords == 0
            ? PricingCoverageStatus.NoData
            : UnpricedRecords == TotalRecords
                ? PricingCoverageStatus.Unpriced
                : PartialRecords > 0 || UnpricedRecords > 0
                    ? PricingCoverageStatus.Partial
                    : PricingCoverageStatus.Complete;
}
