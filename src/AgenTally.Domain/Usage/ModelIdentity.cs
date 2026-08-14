namespace AgenTally.Domain.Usage;

public enum ModelResolutionOrigin
{
    LogConfirmed,
    ProviderModelPair,
    ExactAlias,
    ConfigurationInferred,
    UserMapping,
    Unknown
}

public sealed record ModelIdentity
{
    /// <summary>
    /// Exact model value reported for the provider call. This value is kept
    /// unchanged so that later alias corrections never erase source evidence.
    /// </summary>
    public string? RawModel { get; init; }

    /// <summary>
    /// Canonical local identity used by filters and price matching. It is set
    /// only from source-confirmed identifiers or an exact alias pair.
    /// </summary>
    public string? NormalizedModel { get; init; }

    /// <summary>
    /// Agent-specific route or catalog identifier selected for the call.
    /// </summary>
    public string? RouteModelId { get; init; }

    /// <summary>
    /// Agent-provided display name for the selected model route.
    /// </summary>
    public string? DisplayName { get; init; }

    public string? ProviderId { get; init; }

    public ModelResolutionOrigin ResolutionOrigin { get; init; } = ModelResolutionOrigin.Unknown;
}
