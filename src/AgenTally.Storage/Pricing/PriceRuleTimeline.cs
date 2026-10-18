using System.Collections.Frozen;
using System.Globalization;

namespace AgenTally.Storage.Pricing;

internal static class PriceRuleTimeline
{
    public static FrozenDictionary<string, IReadOnlyList<ResolvedPriceRule>> Build(
        IEnumerable<ResolvedPriceRule> rules)
    {
        var result = new Dictionary<string, IReadOnlyList<ResolvedPriceRule>>(StringComparer.Ordinal);
        foreach (IGrouping<string, ResolvedPriceRule> group in rules.GroupBy(
                     static rule => rule.MatchedModel, StringComparer.Ordinal))
        {
            ResolvedPriceRule[] periods = group.OrderBy(
                static rule => rule.EffectiveFromUtc ?? DateTimeOffset.MinValue).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < periods.Length; index++)
            {
                ResolvedPriceRule period = periods[index];
                ValidateDateBasis(period.EffectiveFromUtc, period.EffectiveFromBasis);
                ValidateDateBasis(period.EffectiveUntilUtc, period.EffectiveUntilBasis);
                if (string.IsNullOrWhiteSpace(period.RuleId) || !ids.Add(period.RuleId) ||
                    period.MatchedModel != period.Rate.NormalizedModel ||
                    ModelPriceRate.NormalizeModel(period.MatchedModel) != period.MatchedModel ||
                    (period.EffectiveFromUtc?.Ticks ?? 0) % TimeSpan.TicksPerMillisecond != 0 ||
                    (period.EffectiveUntilUtc?.Ticks ?? 0) % TimeSpan.TicksPerMillisecond != 0 ||
                    period.EffectiveFromUtc?.Offset is { } startOffset && startOffset != TimeSpan.Zero ||
                    period.EffectiveUntilUtc?.Offset is { } endOffset && endOffset != TimeSpan.Zero ||
                    (period.EffectiveFromUtc ?? DateTimeOffset.MinValue) >=
                    (period.EffectiveUntilUtc ?? DateTimeOffset.MaxValue) ||
                    index > 0 && (periods[index - 1].EffectiveUntilUtc ?? DateTimeOffset.MaxValue) >
                    (period.EffectiveFromUtc ?? DateTimeOffset.MinValue))
                {
                    throw new InvalidDataException($"Invalid or overlapping price periods for '{group.Key}'.");
                }
            }
            result.Add(group.Key, Array.AsReadOnly(periods));
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static bool Contains(ResolvedPriceRule rule, DateTimeOffset instant) =>
        (!rule.EffectiveFromUtc.HasValue || instant >= rule.EffectiveFromUtc.Value) &&
        (!rule.EffectiveUntilUtc.HasValue || instant < rule.EffectiveUntilUtc.Value);

    private static void ValidateDateBasis(DateTimeOffset? boundary, string? basis)
    {
        if (basis is null) return;
        if (boundary is null || basis is not ("official-utc-instant" or
                "official-immediate-announcement" or "official-date-at-utc-midnight" or
                "official-publication-date-at-utc-midnight") ||
            basis.EndsWith("at-utc-midnight", StringComparison.Ordinal) &&
                boundary.Value.TimeOfDay != TimeSpan.Zero)
        {
            throw new InvalidDataException("Invalid price date precision metadata.");
        }
    }

    public static DateTimeOffset? ParseBoundary(string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (!DateTimeOffset.TryParseExact(value,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed) || parsed.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new InvalidDataException("Price boundaries must be explicit ISO UTC timestamps.");
        }
        return parsed;
    }
}
