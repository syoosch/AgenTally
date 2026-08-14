namespace AgenTally.Domain.Usage;

public enum MetricOrigin
{
    Unavailable = 0,
    Exact = 1,
    Derived = 2,
    Inferred = 3,
    UserMapped = 4,
    Estimated = 5,
    Unknown = 6
}
