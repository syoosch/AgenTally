using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors;

public interface IUsageSessionNameSource
{
    Task<IReadOnlyList<UsageSessionNameMetadata>> ReadSessionNamesAsync(
        CancellationToken cancellationToken);
}
