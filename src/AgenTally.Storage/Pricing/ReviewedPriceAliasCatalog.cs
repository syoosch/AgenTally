using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using AgenTally.Domain.Usage;

namespace AgenTally.Storage.Pricing;

/// <summary>
/// Loads exact, manually reviewed redirects from client model identities to
/// existing official API price identities. The redirects never change model
/// identity and never carry a copied rate of their own.
/// </summary>
internal static class ReviewedPriceAliasCatalog
{
    private const string ResourceName =
        "AgenTally.ReviewedPriceAliasCatalog.json";

    public static ReviewedPriceAliasCatalogData Load()
    {
        using Stream stream =
            typeof(ReviewedPriceAliasCatalog).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException(
                $"Embedded reviewed price alias catalog '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(
                "Embedded reviewed price alias catalog did not contain a document.");
        if (document.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(document.CatalogVersion) ||
            document.Aliases is null ||
            document.Aliases.Count == 0)
        {
            throw new InvalidDataException(
                "Embedded reviewed price alias catalog metadata is invalid.");
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CatalogRule rule in document.Aliases)
        {
            ValidateRule(rule);
            if (!aliases.TryAdd(rule.Alias, rule.PricedAs))
            {
                throw new InvalidDataException(
                    $"Reviewed price alias '{rule.Alias}' is duplicated.");
            }
        }

        return new ReviewedPriceAliasCatalogData(
            document.CatalogVersion,
            aliases.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void ValidateRule(CatalogRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Alias) ||
            string.IsNullOrWhiteSpace(rule.PricedAs) ||
            !IsNormalizedCanonical(rule.Alias) ||
            !IsNormalizedCanonical(rule.PricedAs) ||
            string.Equals(
                rule.Alias,
                rule.PricedAs,
                StringComparison.Ordinal) ||
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
                "Embedded reviewed price alias catalog contains an invalid rule.");
        }
    }

    private static bool IsNormalizedCanonical(string model) =>
        string.Equals(
            model,
            model.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        !model.Contains('/') &&
        string.Equals(
            ModelIdentityCanonicalizer.Canonicalize(model),
            model,
            StringComparison.Ordinal);

    private sealed record CatalogDocument(
        int SchemaVersion,
        string CatalogVersion,
        IReadOnlyList<CatalogRule> Aliases);

    private sealed record CatalogRule(
        string Alias,
        string PricedAs,
        string OfficialSource,
        string VerifiedOn);
}

internal sealed record ReviewedPriceAliasCatalogData(
    string Version,
    FrozenDictionary<string, string> Aliases);
