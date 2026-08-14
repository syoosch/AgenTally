using AgenTally.Core.Collectors;

namespace AgenTally.Core.Processing;

public sealed record SyncResult(
    bool Succeeded,
    int AppliedCount,
    int IgnoredCount,
    IReadOnlyList<CollectorDiagnostic> Diagnostics,
    string? Error);
