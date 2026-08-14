using AgenTally.Domain.Usage;

namespace AgenTally.Storage.Pricing;

public static class PriceEstimator
{
    private const decimal TokensPerMillion = 1_000_000m;

    public static EventPriceEstimate Estimate(
        TokenUsage tokens,
        ResolvedPriceRule? rule)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (rule is null)
        {
            return new EventPriceEstimate(
                EventPricingStatus.Unpriced,
                null,
                PricingMissingCategory.ModelRate,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        ModelPriceRate rate = rule.Rate;
        PricingMissingCategory missing = PricingMissingCategory.None;
        decimal inputMultiplier = 1m;
        decimal outputMultiplier = 1m;
        if (rate.LongContextThresholdTokens.HasValue)
        {
            if (tokens.InputReported.Value.HasValue)
            {
                if (tokens.InputReported.Value.Value >
                    rate.LongContextThresholdTokens.Value)
                {
                    inputMultiplier = rate.LongContextInputMultiplier;
                    outputMultiplier = rate.LongContextOutputMultiplier;
                }
            }
            else
            {
                missing |= PricingMissingCategory.LongContextInputTokens;
            }
        }

        decimal amount = 0m;
        long? uncachedInput = ResolveUncachedInput(tokens);
        if (uncachedInput.HasValue)
        {
            amount += Component(
                uncachedInput.Value,
                rate.InputUsdPerMillion,
                inputMultiplier);
        }
        else
        {
            missing |= PricingMissingCategory.UncachedInputTokens;
        }

        long? cachedInput = ResolveCachedInput(tokens);
        if (cachedInput.HasValue)
        {
            if (cachedInput.Value != 0)
            {
                if (rate.CachedInputUsdPerMillion.HasValue)
                {
                    amount += Component(
                        cachedInput.Value,
                        rate.CachedInputUsdPerMillion.Value,
                        inputMultiplier);
                }
                else
                {
                    missing |= PricingMissingCategory.CachedInputRate;
                }
            }
        }
        else
        {
            missing |= PricingMissingCategory.CachedInputTokens;
        }

        if (rate.CacheWriteUsdPerMillion.HasValue)
        {
            if (tokens.CacheWrite.Value.HasValue)
            {
                amount += Component(
                    tokens.CacheWrite.Value.Value,
                    rate.CacheWriteUsdPerMillion.Value,
                    inputMultiplier);
            }
            else
            {
                missing |= PricingMissingCategory.CacheWriteTokens;
            }
        }
        else if (tokens.CacheWrite.Value is > 0)
        {
            missing |= PricingMissingCategory.CacheWriteRate;
        }

        if (tokens.Output.Value.HasValue)
        {
            amount += Component(
                tokens.Output.Value.Value,
                rate.OutputUsdPerMillion,
                outputMultiplier);
        }
        else
        {
            missing |= PricingMissingCategory.OutputTokens;
        }

        return new EventPriceEstimate(
            missing == PricingMissingCategory.None
                ? EventPricingStatus.Complete
                : EventPricingStatus.Partial,
            amount,
            missing,
            rule.CatalogVersion,
            rule.RuleId,
            rate.InputUsdPerMillion,
            rate.CachedInputUsdPerMillion,
            rate.CacheWriteUsdPerMillion,
            rate.OutputUsdPerMillion,
            inputMultiplier,
            outputMultiplier);
    }

    private static long? ResolveUncachedInput(TokenUsage tokens)
    {
        if (tokens.UncachedInput.Value.HasValue)
        {
            return tokens.UncachedInput.Value.Value;
        }

        if (tokens.CacheIncludedInInput == MetricInclusion.Included &&
            tokens.InputReported.Value.HasValue &&
            tokens.CacheRead.Value.HasValue)
        {
            return Math.Max(
                0,
                tokens.InputReported.Value.Value - tokens.CacheRead.Value.Value);
        }

        return null;
    }

    private static long? ResolveCachedInput(TokenUsage tokens)
    {
        if (tokens.CacheRead.Value.HasValue)
        {
            return tokens.CacheRead.Value.Value;
        }

        if (tokens.CacheIncludedInInput == MetricInclusion.Included &&
            tokens.InputReported.Value.HasValue &&
            tokens.UncachedInput.Value.HasValue)
        {
            return Math.Max(
                0,
                tokens.InputReported.Value.Value - tokens.UncachedInput.Value.Value);
        }

        return null;
    }

    private static decimal Component(
        long tokenCount,
        decimal rate,
        decimal multiplier) =>
        tokenCount * rate * multiplier / TokensPerMillion;
}
