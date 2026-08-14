using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace AgenTally.Domain.Usage;

/// <summary>
/// Loads the generated, multi-source exact-alias catalog bundled with the
/// application. It contains identities and provenance only; no price is read
/// from this data.
/// </summary>
internal static class OfflineModelIdentityCatalog
{
    private const string ResourceName = "AgenTally.ModelIdentityCatalog.json";

    private static readonly Lazy<CatalogState> State = new(
        Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Version => State.Value.Version;

    public static int ModelCount => State.Value.ModelCount;

    public static int AliasCount => State.Value.Aliases.Count;

    public static int DataSourceCount => State.Value.DataSourceCount;

    public static int ReferenceProjectCount => State.Value.ReferenceProjectCount;

    public static int CorroboratedAliasCount => State.Value.CorroboratedAliasCount;

    public static bool TryResolve(string normalizedAlias, out string? canonical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedAlias);
        return State.Value.Aliases.TryGetValue(normalizedAlias, out canonical);
    }

    private static CatalogState Load()
    {
        using Stream stream =
            typeof(OfflineModelIdentityCatalog).Assembly
                .GetManifestResourceStream(ResourceName) ??
            throw new InvalidDataException(
                $"Embedded model identity catalog '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<CatalogDocument>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException(
                "Embedded model identity catalog did not contain a document.");
        if (document.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(document.CatalogVersion) ||
            document.DataSources is null ||
            document.DataSources.Count < 2 ||
            document.ReferenceProjects is null ||
            document.ReferenceProjects.Count < 2 ||
            document.ModelCount <= 0 ||
            document.AliasCount <= 0 ||
            document.CorroboratedAliasCount < 0 ||
            document.SingleSourceAliasCount < 0 ||
            document.CorroboratedAliasCount + document.SingleSourceAliasCount !=
                document.AliasCount ||
            document.OmittedConflictCount < 0 ||
            document.OmittedUnmappedQualifiedCount < 0 ||
            document.Aliases is null ||
            document.AliasSources is null ||
            document.AliasCount != document.Aliases.Count ||
            document.AliasCount != document.AliasSources.Count)
        {
            throw new InvalidDataException(
                "Embedded model identity catalog metadata is invalid.");
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (CatalogSourceDocument source in document.DataSources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) ||
                !sourceIds.Add(source.Id))
            {
                throw new InvalidDataException(
                    "Embedded model identity catalog contains an invalid data source.");
            }
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var canonicalModels = new HashSet<string>(StringComparer.Ordinal);
        int corroboratedAliasCount = 0;
        foreach ((string alias, string canonical) in document.Aliases)
        {
            if (string.IsNullOrWhiteSpace(alias) ||
                string.IsNullOrWhiteSpace(canonical) ||
                !string.Equals(
                    alias,
                    alias.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    canonical,
                    canonical.Trim().ToLowerInvariant(),
                    StringComparison.Ordinal) ||
                canonical.Contains('/'))
            {
                throw new InvalidDataException(
                    "Embedded model identity catalog contains an invalid alias.");
            }

            aliases.Add(alias, canonical);
            canonicalModels.Add(canonical);
            if (!document.AliasSources.TryGetValue(
                    alias,
                    out List<string>? evidence) ||
                evidence.Count == 0 ||
                evidence.Distinct(StringComparer.Ordinal).Count() != evidence.Count ||
                evidence.Any(sourceId => !sourceIds.Contains(sourceId)))
            {
                throw new InvalidDataException(
                    "Embedded model identity catalog contains invalid alias evidence.");
            }

            if (evidence.Count > 1)
            {
                corroboratedAliasCount++;
            }
        }

        if (canonicalModels.Count != document.ModelCount ||
            corroboratedAliasCount != document.CorroboratedAliasCount)
        {
            throw new InvalidDataException(
                "Embedded model identity catalog counts do not match its contents.");
        }

        return new CatalogState(
            document.CatalogVersion,
            document.ModelCount,
            document.DataSources.Count,
            document.ReferenceProjects.Count,
            document.CorroboratedAliasCount,
            aliases.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private sealed record CatalogDocument(
        int SchemaVersion,
        string CatalogVersion,
        List<CatalogSourceDocument> DataSources,
        List<CatalogReferenceDocument> ReferenceProjects,
        int ModelCount,
        int AliasCount,
        int CorroboratedAliasCount,
        int SingleSourceAliasCount,
        int OmittedConflictCount,
        int OmittedUnmappedQualifiedCount,
        Dictionary<string, string> Aliases,
        Dictionary<string, List<string>> AliasSources);

    private sealed record CatalogSourceDocument(string Id);

    private sealed record CatalogReferenceDocument(string Id);

    private sealed record CatalogState(
        string Version,
        int ModelCount,
        int DataSourceCount,
        int ReferenceProjectCount,
        int CorroboratedAliasCount,
        FrozenDictionary<string, string> Aliases);
}
