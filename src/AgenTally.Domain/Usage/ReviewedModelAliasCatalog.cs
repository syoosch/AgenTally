using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace AgenTally.Domain.Usage;

/// <summary>
/// Loads exact aliases confirmed from local source structures. These reviewed
/// rules remain separate from the generated market catalog so automated
/// refreshes cannot silently remove or retarget them.
/// </summary>
internal static class ReviewedModelAliasCatalog
{
    private const string ResourceName =
        "AgenTally.ReviewedModelAliasCatalog.json";

    private static readonly Lazy<CatalogState> State = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Version => State.Value.Version;

    public static int Count =>
        State.Value.GlobalAliases.Count + State.Value.SourceAliases.Count;

    public static int GlobalCount => State.Value.GlobalAliases.Count;

    public static int SourceCount => State.Value.SourceAliases.Count;

    public static bool TryResolveGlobal(
        string normalizedAlias,
        out string? canonical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAlias);
        return State.Value.GlobalAliases.TryGetValue(
            normalizedAlias,
            out canonical);
    }

    public static bool TryResolveSource(
        string normalizedAgentId,
        string normalizedAlias,
        out string? canonical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAlias);
        return State.Value.SourceAliases.TryGetValue(
            SourceKey(normalizedAgentId, normalizedAlias),
            out canonical);
    }

    private static CatalogState Load()
    {
        using Stream stream =
            typeof(ReviewedModelAliasCatalog).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException(
                $"Embedded reviewed model alias catalog '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(
                "Embedded reviewed model alias catalog did not contain a document.");
        if (document.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(document.CatalogVersion) ||
            document.GlobalAliases is null ||
            document.SourceAliases is null ||
            document.GlobalAliases.Count + document.SourceAliases.Count == 0)
        {
            throw new InvalidDataException(
                "Embedded reviewed model alias catalog metadata is invalid.");
        }

        var reviewedTargets = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var globalAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CatalogRule rule in document.GlobalAliases)
        {
            ValidateRule(rule);
            if (!globalAliases.TryAdd(rule.Alias, rule.Canonical))
            {
                throw new InvalidDataException(
                    $"Reviewed model alias '{rule.Alias}' is duplicated in global scope.");
            }
            ValidateReviewedAgreement(
                reviewedTargets,
                rule.Alias,
                rule.Canonical);
            ValidateMarketAgreement(rule.Alias, rule.Canonical);
        }

        var sourceAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceCatalogRule rule in document.SourceAliases)
        {
            ValidateRule(rule);
            if (string.IsNullOrWhiteSpace(rule.AgentId) ||
                !string.Equals(
                    rule.AgentId,
                    rule.AgentId.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Embedded reviewed model alias catalog contains an invalid source rule.");
            }

            if (!sourceAliases.TryAdd(
                    SourceKey(rule.AgentId, rule.Alias),
                    rule.Canonical))
            {
                throw new InvalidDataException(
                    $"Reviewed model alias '{rule.Alias}' is duplicated for " +
                    $"agent '{rule.AgentId}'.");
            }
            ValidateReviewedAgreement(
                reviewedTargets,
                rule.Alias,
                rule.Canonical);
            ValidateMarketAgreement(rule.Alias, rule.Canonical);
        }

        return new CatalogState(
            document.CatalogVersion,
            globalAliases.ToFrozenDictionary(StringComparer.Ordinal),
            sourceAliases.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static void ValidateMarketAgreement(
        string alias,
        string canonical)
    {
        if (OfflineModelIdentityCatalog.TryResolve(
                alias,
                out string? marketCanonical) &&
            !string.Equals(
                marketCanonical,
                canonical,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reviewed model alias '{alias}' conflicts with " +
                $"market target '{marketCanonical}'.");
        }
    }

    private static void ValidateReviewedAgreement(
        Dictionary<string, string> targets,
        string alias,
        string canonical)
    {
        if (targets.TryGetValue(alias, out string? existing) &&
            !string.Equals(existing, canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reviewed model alias '{alias}' has conflicting local targets.");
        }

        targets[alias] = canonical;
    }

    private static void ValidateRule(CatalogRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Alias) ||
            string.IsNullOrWhiteSpace(rule.Canonical) ||
            !string.Equals(
                rule.Alias,
                rule.Alias.Trim().ToLowerInvariant(),
                StringComparison.Ordinal) ||
            !string.Equals(
                rule.Canonical,
                rule.Canonical.Trim().ToLowerInvariant(),
                StringComparison.Ordinal) ||
            rule.Canonical.Contains('/') ||
            string.Equals(
                rule.Alias,
                rule.Canonical,
                StringComparison.Ordinal) ||
            rule.Evidence is null ||
            rule.Evidence.Count == 0 ||
            rule.Evidence.Any(string.IsNullOrWhiteSpace) ||
            rule.Evidence.Distinct(StringComparer.Ordinal).Count() !=
                rule.Evidence.Count)
        {
            throw new InvalidDataException(
                "Embedded reviewed model alias catalog contains an invalid rule.");
        }
    }

    private sealed record CatalogDocument(
        int SchemaVersion,
        string CatalogVersion,
        List<CatalogRule> GlobalAliases,
        List<SourceCatalogRule> SourceAliases);

    private record CatalogRule(
        string Alias,
        string Canonical,
        List<string> Evidence);

    private sealed record SourceCatalogRule(
        string AgentId,
        string Alias,
        string Canonical,
        List<string> Evidence) : CatalogRule(Alias, Canonical, Evidence);

    private sealed record CatalogState(
        string Version,
        FrozenDictionary<string, string> GlobalAliases,
        FrozenDictionary<string, string> SourceAliases);

    private static string SourceKey(string agentId, string alias) =>
        $"{agentId}\0{alias}";
}
