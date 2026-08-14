namespace AgenTally.Storage.Pricing;

public interface IPriceLedger
{
    IReadOnlyList<ResolvedPriceRule> GetBuiltInCatalog();

    Task<IReadOnlyList<CustomPriceSetting>> GetCustomPricesAsync(
        CancellationToken cancellationToken);

    Task<int> SetCustomPriceAsync(
        ModelPriceRate rate,
        CancellationToken cancellationToken);

    Task<int> RestoreDefaultAsync(
        string normalizedModel,
        CancellationToken cancellationToken);

    Task<int> RestoreAllDefaultsAsync(
        CancellationToken cancellationToken);
}

public sealed record CustomPriceSetting(
    ModelPriceRate Rate,
    DateTimeOffset UpdatedAtUtc);
