using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors;

public interface IParserVersionedCollector : IAgentCollector
{
    string ParserVersion { get; }

    CompatibilityLevel MaintenanceCompatibilityLevel =>
        CompatibilityLevel.FullyCompatible;

    string? MaintenanceCompatibilityCode => null;
}
