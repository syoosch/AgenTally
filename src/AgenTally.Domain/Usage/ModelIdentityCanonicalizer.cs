namespace AgenTally.Domain.Usage;

/// <summary>
/// Produces the stable model identity used for cross-source grouping.
/// Source values remain available through <see cref="ModelIdentity.RawModel"/>.
/// </summary>
public static class ModelIdentityCanonicalizer
{
    public static string CatalogVersion => OfflineModelIdentityCatalog.Version;

    public static string ReviewedAliasCatalogVersion =>
        ReviewedModelAliasCatalog.Version;

    public static int ReviewedAliasCount => ReviewedModelAliasCatalog.Count;

    public static int ReviewedGlobalAliasCount =>
        ReviewedModelAliasCatalog.GlobalCount;

    public static int ReviewedSourceAliasCount =>
        ReviewedModelAliasCatalog.SourceCount;

    public static int CatalogModelCount => OfflineModelIdentityCatalog.ModelCount;

    public static int CatalogAliasCount => OfflineModelIdentityCatalog.AliasCount;

    public static int CatalogDataSourceCount =>
        OfflineModelIdentityCatalog.DataSourceCount;

    public static int CatalogReferenceProjectCount =>
        OfflineModelIdentityCatalog.ReferenceProjectCount;

    public static int CatalogCorroboratedAliasCount =>
        OfflineModelIdentityCatalog.CorroboratedAliasCount;

    public static string? Canonicalize(
        string? model,
        string? agentId = null,
        string? providerId = null)
    {
        string? canonical = Normalize(model);
        if (canonical is null)
        {
            return null;
        }

        string? normalizedAgent = Normalize(agentId);
        string? normalizedProvider = Normalize(providerId);
        canonical = RemoveRedundantNamespace(
            canonical,
            normalizedAgent,
            normalizedProvider);

        if (ReviewedModelAliasCatalog.TryResolveGlobal(
                canonical,
                out string? confirmedAlias))
        {
            return confirmedAlias;
        }

        if (OfflineModelIdentityCatalog.TryResolve(
                canonical,
                out string? catalogModel))
        {
            return catalogModel;
        }

        return IsBareKimiFamily(canonical)
            ? $"kimi-{canonical}"
            : canonical;
    }

    public static bool TryResolveReviewedSourceAlias(
        string? agentId,
        string? alias,
        out string? canonical)
    {
        canonical = null;
        string? normalizedAgent = Normalize(agentId);
        string? normalizedAlias = Normalize(alias);
        return normalizedAgent is not null &&
               normalizedAlias is not null &&
               ReviewedModelAliasCatalog.TryResolveSource(
                   normalizedAgent,
                   normalizedAlias,
                   out canonical);
    }

    private static string RemoveRedundantNamespace(
        string model,
        string? agentId,
        string? providerId)
    {
        string canonical = model;
        while (true)
        {
            int separator = canonical.IndexOf('/');
            if (separator <= 0 || separator == canonical.Length - 1)
            {
                return canonical;
            }

            string prefix = canonical[..separator];
            if (!string.Equals(prefix, agentId, StringComparison.Ordinal) &&
                !string.Equals(prefix, providerId, StringComparison.Ordinal))
            {
                return canonical;
            }

            canonical = canonical[(separator + 1)..];
        }
    }

    private static bool IsBareKimiFamily(string model)
    {
        if (model.Length < 2 ||
            model[0] != 'k' ||
            model[1] is < '2' or > '9')
        {
            return false;
        }

        return model.Length == 2 || model[2] is '-' or '.';
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}
